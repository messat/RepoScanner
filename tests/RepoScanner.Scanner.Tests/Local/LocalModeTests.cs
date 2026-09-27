using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using RepoScanner.Core.Entities;
using RepoScanner.Core.Enums;
using RepoScanner.Data;
using RepoScanner.Scanner.Secrets;
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

        // Temporary copies of the fixture, deleted after each test
        private readonly List<string> _copies = new List<string>();

        [SetUp]
        public void SetUp()
        {
            _database = new SqliteTestDatabase();
        }

        [TearDown]
        public void TearDown()
        {
            _database.Dispose();

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

        [Test]
        public async Task Path_OnTheFixture_CompletesTheRunWithOneSucceededSecretScannerResult()
        {
            await RunAsync("--path", FixtureSecrets.FolderPath);

            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun run = context.ScanRuns.Include(saved => saved.ScannerResults).Single();
                ScannerResult result = run.ScannerResults.Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(run.Status, Is.EqualTo(ScanRunStatus.Completed));
                    Assert.That(run.NewCount, Is.EqualTo(FixtureSecrets.All.Count));
                    Assert.That(result.ScannerType, Is.EqualTo(ScannerType.Secret));
                    Assert.That(result.Status, Is.EqualTo(ScannerStatus.Succeeded));
                    Assert.That(result.FindingCount, Is.EqualTo(FixtureSecrets.All.Count));
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

        private async Task<(int ExitCode, string Output)> RunAsync(params string[] args)
        {
            int exitCode = 0;
            string output = await ConsoleCapture.RunAsync(async () =>
            {
                exitCode = await Program.RunAsync(args.Concat(DatabaseArgs).ToArray(), services => _database.ReplaceDatabaseIn(services));
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

        private static void AssertExitTwoWithOneLine(int exitCode, string output)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(exitCode, Is.EqualTo(2));
                Assert.That(output.Split('\n').Count(line => line.Contains("FTL]", StringComparison.Ordinal)), Is.EqualTo(1));
                Assert.That(output, Does.Contain("--path"));
                Assert.That(output, Does.Not.Contain("   at "), "A settings problem should not print a stack trace");
            }
        }
    }
}
