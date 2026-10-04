using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using RepoScanner.Core.Entities;
using RepoScanner.Core.Enums;
using RepoScanner.Data;
using RepoScanner.Scanner.Local;
using RepoScanner.Scanner.Secrets;
using RepoScanner.Scanner.Tests.CodePatterns;
using RepoScanner.Scanner.Tests.Packages;
using RepoScanner.Scanner.Tests.Secrets;

namespace RepoScanner.Scanner.Tests.Local
{
    // The whole program in local mode, with in-memory SQLite in place of SQL Server
    [TestFixture]
    public class LocalModeTests
    {
        // Satisfies the start-up check; SQL Server is swapped out, so nothing ever connects to it
        private static readonly string[] DatabaseArgs = { "--Scanner:Database:ConnectionString", "Server=unused;Database=unused" };

        // The file deleted and restored in the Issue 4 tests: it holds two of the fixture's secrets
        private const string DeletedFile = "deploy/publish.ps1";

        private SqliteTestDatabase _database = null!;

        // In place of nuget.org for the package scanner, so no test here reaches the internet
        private FakeNuGet _nuGet = null!;

        // Temporary copies of the fixture, deleted after each test
        private readonly List<string> _copies = new List<string>();

        [SetUp]
        public void SetUp()
        {
            _database = new SqliteTestDatabase();
            _nuGet = new FakeNuGet();
        }

        [TearDown]
        public void TearDown()
        {
            _database.Dispose();
            _nuGet.Dispose();
            Assert.That(_nuGet.UnknownAddresses, Is.Empty, "requests that would have reached the internet");

            foreach (string copy in _copies.Where(Directory.Exists))
            {
                Directory.Delete(copy, recursive: true);
            }

            _copies.Clear();
        }

        // Issue 3 acceptance criterion 1: running against the fixture writes the expected findings to
        // SQL with masked previews
        [Test]
        public async Task Path_OnTheFixture_SavesTheLabelledSecretsWithMaskedPreviews()
        {
            (int exitCode, _) = await RunAsync("--path", FixtureSecrets.FolderPath);

            List<Finding> findings = SavedFindings();
            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(findings.Select(finding => (finding.RuleId, finding.FilePath, finding.LineNumber)),
                Is.EqualTo(FixtureSecrets.All.Select(secret => (secret.RuleId, secret.FilePath, (int?)secret.LineNumber))));

            for (int index = 0; index < findings.Count; index++)
            {
                FixtureSecret secret = FixtureSecrets.All[index];
                string label = secret.RuleId + " in " + secret.FilePath;

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(findings[index].MaskedPreview, Is.EqualTo(SecretMasker.Mask(secret.Value)), label);
                    Assert.That(findings[index].SecretHash, Is.EqualTo(SecretHasher.Hash(secret.Value)), label);
                    Assert.That(findings[index].Status, Is.EqualTo(FindingStatus.Open), label);
                    Assert.That(findings[index].Events.Single().EventType, Is.EqualTo(FindingEventType.Detected), label);
                }
            }
        }

        // All three scanners run (Issue 5, #46; Issue 6, #49). The secrets fixture has no project files, so the
        // package scanner succeeds with nothing to report and asks nuget.org nothing. Its .cs files hold no
        // insecure code patterns.
        [Test]
        public async Task Path_OnTheFixture_CompletesTheRunWithASucceededResultForEachScanner()
        {
            await RunAsync("--path", FixtureSecrets.FolderPath);

            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun run = context.ScanRuns.Include(saved => saved.ScannerResults).Single();
                ScannerResult secrets = run.ScannerResults.Single(result => result.ScannerType == ScannerType.Secret);
                ScannerResult packages = run.ScannerResults.Single(result => result.ScannerType == ScannerType.Package);
                ScannerResult codePatterns = run.ScannerResults.Single(result => result.ScannerType == ScannerType.CodePattern);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(run.Status, Is.EqualTo(ScanRunStatus.Completed));
                    Assert.That(run.NewCount, Is.EqualTo(FixtureSecrets.All.Count));
                    Assert.That(run.ScannerResults, Has.Count.EqualTo(3));
                    Assert.That(secrets.Status, Is.EqualTo(ScannerStatus.Succeeded));
                    Assert.That(secrets.FindingCount, Is.EqualTo(FixtureSecrets.All.Count));
                    Assert.That(packages.Status, Is.EqualTo(ScannerStatus.Succeeded));
                    Assert.That(packages.FindingCount, Is.Zero);
                    Assert.That(codePatterns.Status, Is.EqualTo(ScannerStatus.Succeeded));
                    Assert.That(codePatterns.FindingCount, Is.Zero);
                    Assert.That(_nuGet.Requests, Is.Empty);
                    Assert.That(context.Repositories.Single().ProjectName, Is.EqualTo("Local"));
                }
            }
        }

        // Issue 4 success criterion 1: a second run against an unchanged fixture creates zero new findings
        [Test]
        public async Task Path_OnTheFixtureTwice_AddsNothingTheSecondTime()
        {
            await RunAsync("--path", FixtureSecrets.FolderPath);
            (int exitCode, _) = await RunAsync("--path", FixtureSecrets.FolderPath);

            using (ScannerDbContext context = _database.CreateContext())
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(exitCode, Is.EqualTo(0));
                    Assert.That(context.Findings.Count(), Is.EqualTo(FixtureSecrets.All.Count));
                    Assert.That(context.ScanRuns.OrderBy(run => run.Id).Select(run => run.NewCount),
                        Is.EqualTo(new int[] { FixtureSecrets.All.Count, 0 }));
                }
            }
        }

        // Issue 4 success criterion 2: removing a planted secret resolves it
        [Test]
        public async Task Path_AfterAFileWithSecretsIsDeleted_ResolvesItsFindings()
        {
            string folder = CopyOfTheFixture();
            await RunAsync("--path", folder);

            File.Delete(Path.Combine(folder, DeletedFile));
            (int exitCode, _) = await RunAsync("--path", folder);

            List<Finding> findings = SavedFindings();
            List<Finding> deleted = findings.Where(finding => finding.FilePath == DeletedFile).ToList();
            int deletedCount = FixtureSecrets.All.Count(secret => secret.FilePath == DeletedFile);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(exitCode, Is.EqualTo(0));
                Assert.That(findings, Has.Count.EqualTo(FixtureSecrets.All.Count));
                Assert.That(deleted, Has.Count.EqualTo(deletedCount));
                Assert.That(deleted.Select(finding => finding.Status), Is.All.EqualTo(FindingStatus.Resolved));
                Assert.That(deleted.Select(finding => finding.Events.Count(saved => saved.EventType == FindingEventType.Resolved)), Is.All.EqualTo(1));
                Assert.That(findings.Except(deleted).Select(finding => finding.Status), Is.All.EqualTo(FindingStatus.Open));
                Assert.That(LastRun().ResolvedCount, Is.EqualTo(deletedCount));
            }
        }

        [Test]
        public async Task Path_AfterADeletedFileIsRestored_ReopensItsFindings()
        {
            string folder = CopyOfTheFixture();
            string file = Path.Combine(folder, DeletedFile);
            string aside = Path.Combine(Path.GetDirectoryName(folder)!, "publish.ps1");

            await RunAsync("--path", folder);
            File.Move(file, aside);
            await RunAsync("--path", folder);
            File.Move(aside, file);
            (int exitCode, _) = await RunAsync("--path", folder);

            List<Finding> reopened = SavedFindings().Where(finding => finding.FilePath == DeletedFile).ToList();
            FindingEventType[] history = { FindingEventType.Detected, FindingEventType.Resolved, FindingEventType.Reopened };

            using (Assert.EnterMultipleScope())
            {
                Assert.That(exitCode, Is.EqualTo(0));
                Assert.That(reopened, Has.Count.EqualTo(FixtureSecrets.All.Count(secret => secret.FilePath == DeletedFile)));
                Assert.That(reopened.Select(finding => finding.Status), Is.All.EqualTo(FindingStatus.Open));
                Assert.That(reopened.Select(finding => finding.Events.OrderBy(saved => saved.Id).Select(saved => saved.EventType)), Is.All.EqualTo(history));
                Assert.That(LastRun().ReopenedCount, Is.EqualTo(reopened.Count));
                Assert.That(LastRun().NewCount, Is.Zero);
            }
        }

        // Issue 4 success criterion 3: a suppressed finding stays suppressed
        [Test]
        public async Task Path_OnTheFixtureAgain_KeepsASuppressedFindingSuppressed()
        {
            await RunAsync("--path", FixtureSecrets.FolderPath);

            using (ScannerDbContext context = _database.CreateContext())
            {
                // What the dashboard does when a developer suppresses a finding (ADR-019)
                Finding finding = context.Findings.First(saved => saved.RuleId == "SEC003");
                finding.Status = FindingStatus.Suppressed;
                finding.SuppressedUntilUtc = DateTime.UtcNow.AddDays(14);
                finding.SuppressedBy = "dev@example.com";
                finding.SuppressionReason = "Test value in a sample file";
                context.SaveChanges();
            }

            await RunAsync("--path", FixtureSecrets.FolderPath);

            Finding suppressed = SavedFindings().Single(saved => saved.RuleId == "SEC003");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(suppressed.Status, Is.EqualTo(FindingStatus.Suppressed));
                Assert.That(suppressed.Events.Single().EventType, Is.EqualTo(FindingEventType.Detected));
                Assert.That(LastRun().ResolvedCount, Is.Zero);
            }
        }

        // Issue 3 acceptance criterion 2: no raw value in the saved rows or in the log
        [Test]
        public async Task Path_OnTheFixture_PutsNoRawValueInTheDatabaseOrTheLog()
        {
            (_, string output) = await RunAsync("--path", FixtureSecrets.FolderPath);

            string savedText;
            using (ScannerDbContext context = _database.CreateContext())
            {
                IEnumerable<string> findingRows = context.Findings.AsEnumerable().Select(finding =>
                    string.Join("|", finding.Fingerprint, finding.RuleId, finding.FilePath, finding.Message, finding.MaskedPreview, finding.SecretHash));
                IEnumerable<string> runRows = context.ScanRuns.AsEnumerable().Select(run => run.ErrorMessage ?? string.Empty);
                savedText = string.Join("\n", findingRows.Concat(runRows));
            }

            foreach (FixtureSecret secret in FixtureSecrets.All)
            {
                // Is.False with a message, not Does.Not.Contain: a failure must not print the value itself
                bool inDatabase = savedText.Contains(secret.Value, StringComparison.Ordinal);
                bool inLog = output.Contains(secret.Value, StringComparison.Ordinal);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(inDatabase, Is.False, secret.RuleId + " value from " + secret.FilePath + " leaked into the database");
                    Assert.That(inLog, Is.False, secret.RuleId + " value from " + secret.FilePath + " leaked into the log");
                }
            }
        }

        [Test]
        public async Task Path_ToAFolderThatDoesNotExist_ExitsTwoWithOneLine()
        {
            (int exitCode, string output) = await RunAsync("--path", Path.Combine(FixtureSecrets.FolderPath, "does-not-exist"));

            AssertExitTwoWithOneLine(exitCode, output);
        }

        [Test]
        public async Task Path_WithoutAFolder_ExitsTwoWithOneLine()
        {
            (int exitCode, string output) = await RunAsync("--path");

            AssertExitTwoWithOneLine(exitCode, output);
        }

        // Review focus: a file is not a folder
        [Test]
        public async Task Path_ToAFile_ExitsTwoWithOneLine()
        {
            (int exitCode, string output) = await RunAsync("--path", Path.Combine(FixtureSecrets.FolderPath, "README.md"));

            AssertExitTwoWithOneLine(exitCode, output);
        }

        // Issue 5 criterion 1, through the whole program: the package fixture's vulnerable packages and a file of
        // fake secrets, both saved in one run
        [Test]
        public async Task Path_OnPackagesAndSecrets_SavesBothKindsOfFinding()
        {
            string folder = CopyOfThePackagesFixtureWithSecrets();

            (int exitCode, _) = await RunAsync("--path", folder);

            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun run = context.ScanRuns.Include(saved => saved.ScannerResults).Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(exitCode, Is.EqualTo(0));
                    Assert.That(run.Status, Is.EqualTo(ScanRunStatus.Completed));
                    Assert.That(run.ScannerResults.Select(result => result.Status), Is.All.EqualTo(ScannerStatus.Succeeded));
                    Assert.That(context.Findings.Count(finding => finding.RuleId == "PKG001"), Is.EqualTo(FixturePackages.All.Count(package => package.RuleId == "PKG001")));
                    Assert.That(context.Findings.Count(finding => finding.RuleId == "PKG002"), Is.EqualTo(FixturePackages.All.Count(package => package.RuleId == "PKG002")));
                    Assert.That(context.Findings.Where(finding => finding.ScannerType == ScannerType.Secret).Select(finding => finding.RuleId),
                        Is.EquivalentTo(new[] { "SEC004", "SEC006" }));
                }
            }
        }

        // Issue 5 criteria 2 and 3, and Issue 4's fourth criterion end to end (#47): with nuget.org down the run is
        // PartiallyCompleted, not Failed; the removed secrets are resolved, because the secret scanner looked; the
        // package findings stay Open, because the package scanner could not look (ADR-025)
        [Test]
        public async Task Path_WhenNuGetOrgGoesDown_IsPartiallyCompletedAndOnlyTheSecretsResolve()
        {
            string folder = CopyOfThePackagesFixtureWithSecrets();
            await RunAsync("--path", folder);

            File.Delete(Path.Combine(folder, DeletedFile));
            _nuGet.Fail();
            (int exitCode, string output) = await RunAsync("--path", folder);

            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun run = context.ScanRuns.Include(saved => saved.ScannerResults).OrderBy(saved => saved.Id).Last();
                ScannerResult packages = run.ScannerResults.Single(result => result.ScannerType == ScannerType.Package);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(exitCode, Is.EqualTo(0));
                    Assert.That(run.Status, Is.EqualTo(ScanRunStatus.PartiallyCompleted));
                    Assert.That(run.ErrorMessage, Does.StartWith("Package scanner failed: nuget.org data could not be downloaded from https://api.nuget.org/v3/index.json"));
                    Assert.That(run.ResolvedCount, Is.EqualTo(2));
                    Assert.That(packages.Status, Is.EqualTo(ScannerStatus.Failed));
                    Assert.That(run.ScannerResults.Single(result => result.ScannerType == ScannerType.Secret).Status, Is.EqualTo(ScannerStatus.Succeeded));
                    Assert.That(context.Findings.Where(finding => finding.ScannerType == ScannerType.Secret).Select(finding => finding.Status),
                        Is.All.EqualTo(FindingStatus.Resolved));
                    Assert.That(context.Findings.Where(finding => finding.ScannerType == ScannerType.Package).Select(finding => finding.Status),
                        Is.All.EqualTo(FindingStatus.Open));
                    Assert.That(context.Findings.Count(finding => finding.ScannerType == ScannerType.Package), Is.EqualTo(FixturePackages.All.Count));
                    Assert.That(output, Does.Contain("PartiallyCompleted"));
                }
            }
        }

        // Issue 10 review: a --path scan's lines carry its correlation ID and folder through the whole program too, so a
        // local scan is as searchable as a queued one; the ID is the one its scan run saved
        [Test]
        public async Task Path_EveryLineOfTheScan_CarriesItsCorrelationIdAndFolder()
        {
            (int exitCode, string output) = await RunAsync("--path", FixtureSecrets.FolderPath);

            IReadOnlyList<JsonElement> lines = LogLines.Read(output);
            string correlationId = LastRun().CorrelationId.ToString();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(exitCode, Is.EqualTo(0));
                Assert.That(lines, Is.Not.Empty);
                Assert.That(lines.Select(line => LogLines.Value(line, "CorrelationId")), Is.All.EqualTo(correlationId));
                Assert.That(lines.Select(line => LogLines.Value(line, "ProjectName")), Is.All.EqualTo(LocalScanRunner.LocalProjectName));
                Assert.That(lines.Select(line => LogLines.Value(line, "RepositoryName")), Is.All.EqualTo("secrets-repo"));
            }
        }

        // Issue 10 design, decision 11, and Review Focus 5: each nuget.org request's own lines are gone, which were most of a
        // run's lines, but a retry, which says nuget.org is struggling, still shows
        [Test]
        public async Task Path_WhenNuGetOrgGoesDown_LogsTheRetriesButNotEachRequest()
        {
            string folder = CopyOfThePackagesFixtureWithSecrets();
            _nuGet.Fail();

            (int exitCode, string output) = await RunAsync("--path", folder);

            JsonElement[] http = LogLines.Read(output).Where(line =>
                (LogLines.Value(line, "SourceContext") ?? string.Empty).StartsWith("System.Net.Http.HttpClient", StringComparison.Ordinal)
                || (LogLines.Value(line, "SourceContext") ?? string.Empty).StartsWith("Polly", StringComparison.Ordinal)).ToArray();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(exitCode, Is.EqualTo(0));
                Assert.That(http.Where(line => LogLines.Level(line) == "Information"), Is.Empty);
                Assert.That(http.Where(line => LogLines.Value(line, "SourceContext")!.StartsWith("Polly", StringComparison.Ordinal) && LogLines.Level(line) == "Warning"),
                    Is.Not.Empty);
            }
        }

        // Issue 6 criterion 2, through the whole program (#54): exactly the planted patterns saved to SQL, and nothing
        // from the secret or package scanners. A second run adds nothing, because each finding keeps its
        // fingerprint (ADR-024), including the two empty catches of one file.
        [Test]
        public async Task Path_OnThePatternsFixture_SavesExactlyThePlantedPatternsOnce()
        {
            (int exitCode, _) = await RunAsync("--path", FixturePatterns.FolderPath);
            (int secondExitCode, _) = await RunAsync("--path", FixturePatterns.FolderPath);

            List<Finding> findings = SavedFindings();
            using (ScannerDbContext context = _database.CreateContext())
            {
                List<ScanRun> runs = context.ScanRuns.Include(saved => saved.ScannerResults).OrderBy(saved => saved.Id).ToList();
                ScannerResult codePatterns = runs[0].ScannerResults.Single(result => result.ScannerType == ScannerType.CodePattern);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(exitCode, Is.EqualTo(0));
                    Assert.That(secondExitCode, Is.EqualTo(0));
                    Assert.That(findings.Select(finding => (finding.RuleId, finding.FilePath, finding.LineNumber)),
                        Is.EqualTo(FixturePatterns.All.Select(pattern => (pattern.RuleId, pattern.FilePath, (int?)pattern.LineNumber))));
                    Assert.That(findings.Select(finding => finding.ScannerType), Is.All.EqualTo(ScannerType.CodePattern));
                    Assert.That(findings.Select(finding => finding.Status), Is.All.EqualTo(FindingStatus.Open));
                    Assert.That(runs.Select(run => run.Status), Is.All.EqualTo(ScanRunStatus.Completed));
                    Assert.That(codePatterns.FindingCount, Is.EqualTo(FixturePatterns.All.Count));
                    Assert.That(runs.Select(run => run.NewCount), Is.EqualTo(new int[] { FixturePatterns.All.Count, 0 }));
                    Assert.That(_nuGet.Requests, Is.Empty);
                }
            }
        }

        // Issue 11 (#68): an empty repository through the whole program. The walker's own test covers an empty
        // folder; this one checks every scanner and the saved run.
        [Test]
        public async Task Path_OnAnEmptyFolder_CompletesWithNoFindings()
        {
            using (TempFolder folder = new TempFolder())
            {
                (int exitCode, _) = await RunAsync("--path", folder.FullPath);

                using (ScannerDbContext context = _database.CreateContext())
                {
                    ScanRun run = context.ScanRuns.Include(saved => saved.ScannerResults).Single();

                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(exitCode, Is.EqualTo(0));
                        Assert.That(run.Status, Is.EqualTo(ScanRunStatus.Completed));
                        Assert.That(run.NewCount, Is.Zero);
                        Assert.That(context.Findings, Is.Empty);
                        Assert.That(run.ScannerResults, Has.Count.EqualTo(3));
                        Assert.That(run.ScannerResults.Select(result => result.Status), Is.All.EqualTo(ScannerStatus.Succeeded));
                        Assert.That(run.ScannerResults.Select(result => result.FindingCount), Is.All.EqualTo(0));
                        Assert.That(_nuGet.Requests, Is.Empty);
                    }
                }
            }
        }

        // Issue 11 (#68): a minified file is one long line. Each secret pattern stops after 1 second and is then
        // skipped for that file, so a slow pattern would miss a secret at the end without failing the scan.
        [Test]
        public async Task Path_OnAFileWithOneHugeLine_FindsTheSecretAtItsEnd()
        {
            // The largest file the walker scans
            const int LargestScannedFile = 1048576;
            const string PlantedValue = "FAKE-Huge-Line-Pa55word";

            // Separate literals, and a constant whose name is not a secret word, so this repository's own scan
            // (SEC003 and SEC007) does not report this file.
            // The comma keeps a word boundary before Server wherever the filler is cut.
            string tail = ",Server=db01;Database=Orders;" + "Password=" + PlantedValue;
            string filler = string.Concat(Enumerable.Repeat("{\"id\":1,\"name\":\"item\"},", 50000));

            using (TempFolder folder = new TempFolder())
            {
                string path = folder.WriteText("wwwroot/app.min.js", filler.Substring(0, LargestScannedFile - tail.Length) + tail);

                (int exitCode, string output) = await RunAsync("--path", folder.FullPath);

                List<Finding> findings = SavedFindings();
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(new FileInfo(path).Length, Is.EqualTo(LargestScannedFile));
                    Assert.That(exitCode, Is.EqualTo(0));
                    Assert.That(findings.Select(finding => (finding.RuleId, finding.FilePath, finding.LineNumber)),
                        Is.EqualTo(new[] { ("SEC003", "wwwroot/app.min.js", (int?)1) }));
                    Assert.That(findings.Single().SecretHash, Is.EqualTo(SecretHasher.Hash(PlantedValue)));
                    Assert.That(LastRun().Status, Is.EqualTo(ScanRunStatus.Completed));
                    Assert.That(output, Does.Not.Contain("timed out"));
                }
            }
        }

        private async Task<(int ExitCode, string Output)> RunAsync(params string[] args)
        {
            int exitCode = 0;
            string output = await ConsoleCapture.RunAsync(async () =>
            {
                exitCode = await Program.RunAsync(args.Concat(DatabaseArgs).ToArray(), services =>
                {
                    _database.ReplaceDatabaseIn(services);
                    _nuGet.ReplaceNuGetOrg(services);
                });
            });

            return (exitCode, output);
        }

        // Sorted in memory by ordinal, the order FixtureSecrets lists them in
        private List<Finding> SavedFindings()
        {
            using (ScannerDbContext context = _database.CreateContext())
            {
                return context.Findings.Include(finding => finding.Events).AsEnumerable()
                    .OrderBy(finding => finding.FilePath, StringComparer.Ordinal)
                    .ThenBy(finding => finding.LineNumber)
                    .ToList();
            }
        }

        private ScanRun LastRun()
        {
            using (ScannerDbContext context = _database.CreateContext())
            {
                return context.ScanRuns.OrderBy(run => run.Id).Last();
            }
        }

        // A copy of the fixture in a new temporary folder, so a test can delete and restore its files.
        // The copy sits one level down, so a file moved aside is outside the scanned folder.
        private string CopyOfTheFixture()
        {
            string root = Path.Combine(Path.GetTempPath(), "reposcanner-tests", Guid.NewGuid().ToString("N"));
            string folder = Path.Combine(root, "secrets-repo");
            _copies.Add(root);

            foreach (string source in Directory.EnumerateFiles(FixtureSecrets.FolderPath, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(folder, Path.GetRelativePath(FixtureSecrets.FolderPath, source));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
            }

            return folder;
        }

        // A copy of the packages fixture with the secrets fixture's deploy/publish.ps1 added, which holds two fake
        // secrets (SEC004 and SEC006), so one folder gives both kinds of finding
        private string CopyOfThePackagesFixtureWithSecrets()
        {
            string root = Path.Combine(Path.GetTempPath(), "reposcanner-tests", Guid.NewGuid().ToString("N"));
            string folder = Path.Combine(root, "packages-and-secrets");
            _copies.Add(root);

            foreach (string source in Directory.EnumerateFiles(FixturePackages.FolderPath, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(folder, Path.GetRelativePath(FixturePackages.FolderPath, source));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
            }

            string secrets = Path.Combine(folder, DeletedFile);
            Directory.CreateDirectory(Path.GetDirectoryName(secrets)!);
            File.Copy(Path.Combine(FixtureSecrets.FolderPath, DeletedFile), secrets);

            return folder;
        }

        private static void AssertExitTwoWithOneLine(int exitCode, string output)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(exitCode, Is.EqualTo(2));
                Assert.That(LogLines.Read(output).Count(line => LogLines.Level(line) == "Fatal"), Is.EqualTo(1));
                Assert.That(output, Does.Contain("--path"));
                Assert.That(output, Does.Not.Contain("   at "), "A settings problem should not print a stack trace");
            }
        }
    }
}
