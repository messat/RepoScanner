using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using NUnit.Framework;
using RepoScanner.Core.Entities;
using RepoScanner.Core.Enums;
using RepoScanner.Core.Scanning;
using RepoScanner.Data;
using RepoScanner.Data.Repositories;
using RepoScanner.Scanner.Local;

namespace RepoScanner.Scanner.Tests.Local
{
    [TestFixture]
    public class LocalScanRunnerTests
    {
        // The runner never reads this folder itself: the stub scanner stands in for the real one
        private static readonly string Folder = Path.Combine(Path.GetTempPath(), "orders-api");

        private SqliteTestDatabase _database = null!;
        private FakeLogger<LocalScanRunner> _logger = null!;

        [SetUp]
        public void SetUp()
        {
            _database = new SqliteTestDatabase();
            _logger = new FakeLogger<LocalScanRunner>();
        }

        [TearDown]
        public void TearDown()
        {
            _database.Dispose();
        }

        [Test]
        public async Task RunAsync_AddsTheFolderAsALocalRepository()
        {
            await RunAsync(new StubScanner());

            using (ScannerDbContext context = _database.CreateContext())
            {
                Repository repository = context.Repositories.Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(repository.ProjectName, Is.EqualTo("Local"));
                    Assert.That(repository.Name, Is.EqualTo("orders-api"));
                    Assert.That(repository.DefaultBranch, Is.EqualTo("local"));
                    Assert.That(repository.AzureDevOpsId, Is.EqualTo(LocalScanRunner.DescribeFolder(Folder).AzureDevOpsId));
                }
            }
        }

        [Test]
        public async Task RunAsync_SavesEachFindingAsOpenWithADetectedEvent()
        {
            await RunAsync(new StubScanner(Raw("SEC003", "src/appsettings.json", 3, "hash-a")));

            using (ScannerDbContext context = _database.CreateContext())
            {
                Finding finding = context.Findings.Include(saved => saved.Events).Single();
                ScanRun run = context.ScanRuns.Single();
                FindingEvent detected = finding.Events.Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(finding.RuleId, Is.EqualTo("SEC003"));
                    Assert.That(finding.FilePath, Is.EqualTo("src/appsettings.json"));
                    Assert.That(finding.LineNumber, Is.EqualTo(3));
                    Assert.That(finding.MaskedPreview, Is.EqualTo("FAKE****"));
                    Assert.That(finding.SecretHash, Is.EqualTo("hash-a"));
                    Assert.That(finding.Status, Is.EqualTo(FindingStatus.Open));
                    Assert.That(finding.Fingerprint, Does.Match("^[0-9a-f]{64}$"));
                    Assert.That(finding.LastSeenUtc, Is.EqualTo(finding.FirstSeenUtc));
                    Assert.That(detected.EventType, Is.EqualTo(FindingEventType.Detected));
                    Assert.That(detected.Actor, Is.EqualTo("scanner"));
                    Assert.That(detected.ScanRunId, Is.EqualTo(run.Id));
                }
            }
        }

        [Test]
        public async Task RunAsync_CompletesTheRunWithTheNewCountAndOneScannerResult()
        {
            await RunAsync(new StubScanner(Raw("SEC003", "a.json", 3, "hash-a"), Raw("SEC007", "a.json", 6, "hash-b")));

            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun run = context.ScanRuns.Include(saved => saved.ScannerResults).Single();
                ScannerResult result = run.ScannerResults.Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(run.Status, Is.EqualTo(ScanRunStatus.Completed));
                    Assert.That(run.TriggerType, Is.EqualTo(ScanTrigger.OnDemand));
                    Assert.That(run.NewCount, Is.EqualTo(2));
                    Assert.That(run.ReopenedCount, Is.Zero);
                    Assert.That(run.ResolvedCount, Is.Zero);
                    Assert.That(run.CommitSha, Is.Null);
                    Assert.That(run.CompletedUtc, Is.Not.Null);
                    Assert.That(result.ScannerType, Is.EqualTo(ScannerType.Secret));
                    Assert.That(result.Status, Is.EqualTo(ScannerStatus.Succeeded));
                    Assert.That(result.FindingCount, Is.EqualTo(2));
                }
            }
        }

        // Issue 4 success criterion 1: a second run of the same folder adds nothing, rather than failing on
        // the unique fingerprint
        [Test]
        public async Task RunAsync_OnTheSameFolderTwice_AddsNothingTheSecondTime()
        {
            RawFinding raw = Raw("SEC003", "src/appsettings.json", 3, "hash-a");

            await RunAsync(new StubScanner(raw));
            await RunAsync(new StubScanner(raw));

            using (ScannerDbContext context = _database.CreateContext())
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(context.Findings.Count(), Is.EqualTo(1));
                    Assert.That(context.Repositories.Count(), Is.EqualTo(1));
                    Assert.That(context.ScanRuns.OrderBy(run => run.Id).Select(run => run.NewCount), Is.EqualTo(new int[] { 1, 0 }));
                    Assert.That(context.ScanRuns.Select(run => run.Status), Is.All.EqualTo(ScanRunStatus.Completed));
                }
            }
        }

        // The fingerprint has no line number, so the same secret twice in one file is one finding
        [Test]
        public async Task RunAsync_WithTheSameSecretTwiceInOneFile_SavesOneFinding()
        {
            await RunAsync(new StubScanner(Raw("SEC007", "a.json", 2, "hash-a"), Raw("SEC007", "a.json", 9, "hash-a")));

            using (ScannerDbContext context = _database.CreateContext())
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(context.Findings.Single().LineNumber, Is.EqualTo(2));
                    Assert.That(context.ScanRuns.Single().NewCount, Is.EqualTo(1));
                }
            }
        }

        // A finding seen again keeps its row: only the line it is now on and when it was last seen change
        [Test]
        public async Task RunAsync_WhenAFindingIsFoundAgain_UpdatesItsLineAndLastSeen()
        {
            await RunAsync(new StubScanner(Raw("SEC003", "src/appsettings.json", 3, "hash-a")));
            await RunAsync(new StubScanner(Raw("SEC003", "src/appsettings.json", 7, "hash-a")));

            using (ScannerDbContext context = _database.CreateContext())
            {
                Finding finding = context.Findings.Include(saved => saved.Events).Single();
                ScanRun second = context.ScanRuns.OrderBy(run => run.Id).Last();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(finding.LineNumber, Is.EqualTo(7));
                    Assert.That(finding.LastSeenUtc, Is.GreaterThanOrEqualTo(second.StartedUtc));
                    Assert.That(finding.Status, Is.EqualTo(FindingStatus.Open));
                    Assert.That(finding.Events.Single().EventType, Is.EqualTo(FindingEventType.Detected));
                }
            }
        }

        // Issue 4 success criterion 2, with a stub scanner
        [Test]
        public async Task RunAsync_WhenAFindingIsNoLongerFound_ResolvesIt()
        {
            await RunAsync(new StubScanner(Raw("SEC003", "src/appsettings.json", 3, "hash-a")));
            await RunAsync(new StubScanner());

            using (ScannerDbContext context = _database.CreateContext())
            {
                Finding finding = context.Findings.Include(saved => saved.Events).Single();
                ScanRun second = context.ScanRuns.OrderBy(run => run.Id).Last();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(finding.Status, Is.EqualTo(FindingStatus.Resolved));
                    Assert.That(finding.ResolvedUtc, Is.Not.Null);
                    Assert.That(EventTypes(finding), Is.EqualTo(new[] { FindingEventType.Detected, FindingEventType.Resolved }));
                    Assert.That(finding.Events.Single(saved => saved.EventType == FindingEventType.Resolved).ScanRunId, Is.EqualTo(second.Id));
                    Assert.That(second.ResolvedCount, Is.EqualTo(1));
                    Assert.That(second.NewCount, Is.Zero);
                }
            }
        }

        [Test]
        public async Task RunAsync_WhenAResolvedFindingIsFoundAgain_ReopensIt()
        {
            RawFinding raw = Raw("SEC003", "src/appsettings.json", 3, "hash-a");

            await RunAsync(new StubScanner(raw));
            await RunAsync(new StubScanner());
            await RunAsync(new StubScanner(raw));

            using (ScannerDbContext context = _database.CreateContext())
            {
                Finding finding = context.Findings.Include(saved => saved.Events).Single();
                ScanRun third = context.ScanRuns.OrderBy(run => run.Id).Last();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(finding.Status, Is.EqualTo(FindingStatus.Open));
                    Assert.That(finding.ResolvedUtc, Is.Null);
                    Assert.That(EventTypes(finding), Is.EqualTo(new[] { FindingEventType.Detected, FindingEventType.Resolved, FindingEventType.Reopened }));
                    Assert.That(third.ReopenedCount, Is.EqualTo(1));
                    Assert.That(third.NewCount, Is.Zero);
                }
            }
        }

        // Issue 4 success criterion 3: a suppression set in the dashboard survives the next scan
        [Test]
        public async Task RunAsync_WhenAFindingIsSuppressed_KeepsItSuppressed()
        {
            RawFinding raw = Raw("SEC003", "src/appsettings.json", 3, "hash-a");

            await RunAsync(new StubScanner(raw));
            SuppressTheFinding(DateTime.UtcNow.AddDays(14));
            await RunAsync(new StubScanner(raw));

            using (ScannerDbContext context = _database.CreateContext())
            {
                Finding finding = context.Findings.Include(saved => saved.Events).Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(finding.Status, Is.EqualTo(FindingStatus.Suppressed));
                    Assert.That(finding.SuppressionReason, Is.EqualTo("Test value in a sample file"));
                    Assert.That(EventTypes(finding), Is.EqualTo(new[] { FindingEventType.Detected }));
                }
            }
        }

        // Rule 7: only the scanners that ran can resolve, so a package finding, which a secret scanner could
        // never have seen, is left alone when only a secret scanner runs (ADR-025)
        [Test]
        public async Task RunAsync_LeavesTheFindingsOfScannersThatDidNotRunAlone()
        {
            await RunAsync(new StubScanner());
            AddAPackageFinding();
            await RunAsync(new StubScanner());

            using (ScannerDbContext context = _database.CreateContext())
            {
                Finding finding = context.Findings.Include(saved => saved.Events).Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(finding.Status, Is.EqualTo(FindingStatus.Open));
                    Assert.That(finding.Events, Is.Empty);
                    Assert.That(context.ScanRuns.OrderBy(run => run.Id).Last().ResolvedCount, Is.Zero);
                }
            }
        }

        // ADR-013: a failed scan is always visible in ScanRuns
        [Test]
        public async Task RunAsync_WhenAScannerFails_MarksTheRunFailedAndRethrows()
        {
            await Assert.ThatAsync(() => RunAsync(new StubScanner(new InvalidOperationException("Simulated failure"))),
                Throws.InvalidOperationException);

            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun run = context.ScanRuns.Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(run.Status, Is.EqualTo(ScanRunStatus.Failed));
                    Assert.That(run.ErrorMessage, Is.EqualTo("Simulated failure"));
                    Assert.That(run.CompletedUtc, Is.Not.Null);
                }
            }
        }

        // #74: one scanner failing, here the package scanner with nuget.org down, does not stop the others
        [Test]
        public async Task RunAsync_WhenOneScannerFails_TheOthersFindingsAreStillSaved()
        {
            StubScanner packages = FailingPackageScanner("nuget.org data could not be downloaded");

            await RunAsync(new StubScanner(Raw("SEC003", "src/appsettings.json", 3, "hash-a")), packages);

            using (ScannerDbContext context = _database.CreateContext())
            {
                Assert.That(context.Findings.Select(finding => finding.RuleId), Is.EqualTo(new[] { "SEC003" }));
            }
        }

        [Test]
        public async Task RunAsync_WhenOneScannerFails_RecordsAFailedResultWithItsError()
        {
            await RunAsync(new StubScanner(Raw("SEC003", "src/appsettings.json", 3, "hash-a")), FailingPackageScanner("nuget.org data could not be downloaded"));

            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun run = context.ScanRuns.Include(saved => saved.ScannerResults).Single();
                ScannerResult secrets = run.ScannerResults.Single(result => result.ScannerType == ScannerType.Secret);
                ScannerResult packages = run.ScannerResults.Single(result => result.ScannerType == ScannerType.Package);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(secrets.Status, Is.EqualTo(ScannerStatus.Succeeded));
                    Assert.That(secrets.FindingCount, Is.EqualTo(1));
                    Assert.That(packages.Status, Is.EqualTo(ScannerStatus.Failed));
                    Assert.That(packages.FindingCount, Is.Zero);
                    Assert.That(packages.ErrorMessage, Is.EqualTo("nuget.org data could not be downloaded"));
                }
            }
        }

        // Issue 4's fourth criterion, now at the runner (ADR-025): the secret scanner completed, so its missing
        // finding is resolved; the package scanner failed, so its finding, not seen this time, stays Open
        [Test]
        public async Task RunAsync_WhenOneScannerFails_OnlyTheScannersThatSucceededResolveFindings()
        {
            await RunAsync(new StubScanner(Raw("SEC003", "src/appsettings.json", 3, "hash-a")));
            AddAPackageFinding();

            await RunAsync(new StubScanner(), FailingPackageScanner("nuget.org data could not be downloaded"));

            using (ScannerDbContext context = _database.CreateContext())
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(context.Findings.Single(finding => finding.ScannerType == ScannerType.Secret).Status, Is.EqualTo(FindingStatus.Resolved));
                    Assert.That(context.Findings.Single(finding => finding.ScannerType == ScannerType.Package).Status, Is.EqualTo(FindingStatus.Open));
                    Assert.That(context.ScanRuns.OrderBy(run => run.Id).Last().ResolvedCount, Is.EqualTo(1));
                }
            }
        }

        [Test]
        public async Task RunAsync_WhenOneScannerFails_LogsAWarningNamingIt()
        {
            await RunAsync(new StubScanner(), FailingPackageScanner("nuget.org data could not be downloaded"));

            FakeLogRecord warning = _logger.Collector.GetSnapshot().Single(record => record.Level == LogLevel.Warning);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(warning.GetStructuredStateValue("ScannerType"), Is.EqualTo("Package"));
                Assert.That(warning.Exception, Is.InstanceOf<InvalidOperationException>());
            }
        }

        // Stopping the scanner (Ctrl+C, or SIGTERM in Azure) is not a scanner failing: the run stops, and the
        // scanners after it do not run
        [Test]
        public async Task RunAsync_WhenTheRunIsCancelled_StopsAndMarksTheRunFailed()
        {
            using (CancellationTokenSource stopping = new CancellationTokenSource())
            {
                // Stopped while the first scanner runs, as Ctrl+C would, after the run has started
                StubScanner cancelled = new StubScanner(new OperationCanceledException(stopping.Token)) { OnScan = stopping.Cancel };
                StubScanner after = new StubScanner() { ScannerType = ScannerType.Package };

                await Assert.ThatAsync(() => RunAsync(stopping.Token, cancelled, after), Throws.InstanceOf<OperationCanceledException>());

                using (ScannerDbContext context = _database.CreateContext())
                {
                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(after.CallCount, Is.Zero);
                        Assert.That(context.ScanRuns.Single().Status, Is.EqualTo(ScanRunStatus.Failed));
                    }
                }
            }
        }

        // A timeout inside a scanner can surface as a TaskCanceledException while the run itself carries on:
        // that is the scanner failing, not the run being stopped
        [Test]
        public async Task RunAsync_ACancellationTheRunDidNotAskFor_IsThatScannerFailing()
        {
            StubScanner timedOut = new StubScanner(new TaskCanceledException("The request timed out")) { ScannerType = ScannerType.Package };

            await RunAsync(new StubScanner(Raw("SEC003", "src/appsettings.json", 3, "hash-a")), timedOut);

            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun run = context.ScanRuns.Include(saved => saved.ScannerResults).Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(run.ScannerResults.Single(result => result.ScannerType == ScannerType.Package).Status, Is.EqualTo(ScannerStatus.Failed));
                    Assert.That(context.Findings.Count(), Is.EqualTo(1));
                }
            }
        }

        // ADR-013 again, when it is the save that fails: here the database rejects a finding with no
        // message (NOT NULL), and the run must still end up Failed rather than Running for ever
        [Test]
        public async Task RunAsync_WhenSavingTheFindingsFails_StillMarksTheRunFailed()
        {
            RawFinding rejected = Raw("SEC003", "src/appsettings.json", 3, "hash-a") with { Message = null! };

            await Assert.ThatAsync(() => RunAsync(new StubScanner(rejected)), Throws.InstanceOf<DbUpdateException>());

            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun run = context.ScanRuns.Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(run.Status, Is.EqualTo(ScanRunStatus.Failed));
                    Assert.That(run.CompletedUtc, Is.Not.Null);
                    Assert.That(context.Findings.Count(), Is.Zero);
                }
            }
        }

        // Log lines name the rule, file and line: never the preview, or anything else from the match
        [Test]
        public async Task RunAsync_LogsEachNewFindingWithoutItsPreview()
        {
            await RunAsync(new StubScanner(Raw("SEC003", "src/appsettings.json", 3, "hash-a")));

            string line = _logger.Collector.GetSnapshot()
                .Select(record => record.Message)
                .Single(message => message.StartsWith("New finding", StringComparison.Ordinal));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(line, Is.EqualTo("New finding SEC003 in src/appsettings.json at line 3"));
                Assert.That(line, Does.Not.Contain("FAKE****"));
            }
        }

        // The same for resolved and reopened findings
        [Test]
        public async Task RunAsync_LogsEachResolvedAndReopenedFindingWithoutItsPreview()
        {
            RawFinding raw = Raw("SEC003", "src/appsettings.json", 3, "hash-a");

            await RunAsync(new StubScanner(raw));
            await RunAsync(new StubScanner());
            await RunAsync(new StubScanner(raw));

            string[] messages = _logger.Collector.GetSnapshot().Select(record => record.Message).ToArray();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(messages, Has.One.EqualTo("Resolved finding SEC003 in src/appsettings.json at line 3"));
                Assert.That(messages, Has.One.EqualTo("Reopened finding SEC003 in src/appsettings.json at line 3"));
                Assert.That(messages, Has.None.Contains("FAKE****"));
            }
        }

        // Review focus: however the folder is written, it is the same Repositories row
        [Test]
        public void DescribeFolder_GivesTheSameIdHoweverTheFolderIsWritten()
        {
            Guid id = LocalScanRunner.DescribeFolder(Folder).AzureDevOpsId;
            string relative = Path.GetRelativePath(Environment.CurrentDirectory, Folder);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(LocalScanRunner.DescribeFolder(Folder + Path.DirectorySeparatorChar).AzureDevOpsId, Is.EqualTo(id));
                Assert.That(LocalScanRunner.DescribeFolder(relative).AzureDevOpsId, Is.EqualTo(id));
                Assert.That(LocalScanRunner.DescribeFolder(Folder.ToUpperInvariant()).AzureDevOpsId, Is.EqualTo(id));
            }
        }

        [Test]
        public void DescribeFolder_GivesDifferentFoldersDifferentIds()
        {
            Guid first = LocalScanRunner.DescribeFolder(Folder).AzureDevOpsId;
            Guid second = LocalScanRunner.DescribeFolder(Folder + "-copy").AzureDevOpsId;

            Assert.That(second, Is.Not.EqualTo(first));
        }

        private Task RunAsync(params IScanner[] scanners)
        {
            return RunAsync(CancellationToken.None, scanners);
        }

        private async Task RunAsync(CancellationToken cancellationToken, params IScanner[] scanners)
        {
            using (ScannerDbContext context = _database.CreateContext())
            {
                LocalScanRunner runner = new LocalScanRunner(new ScanRunRepository(context), new FindingRepository(context), scanners, _logger);

                await runner.RunAsync(Folder, cancellationToken);
            }
        }

        // The package scanner when nuget.org cannot be reached
        private static StubScanner FailingPackageScanner(string message)
        {
            return new StubScanner(new InvalidOperationException(message)) { ScannerType = ScannerType.Package };
        }

        // What the dashboard does when a developer suppresses a finding (ADR-019)
        private void SuppressTheFinding(DateTime suppressedUntilUtc)
        {
            using (ScannerDbContext context = _database.CreateContext())
            {
                Finding finding = context.Findings.Single();
                finding.Status = FindingStatus.Suppressed;
                finding.SuppressedUntilUtc = suppressedUntilUtc;
                finding.SuppressedBy = "dev@example.com";
                finding.SuppressionReason = "Test value in a sample file";
                context.SaveChanges();
            }
        }

        // A package finding for the scanned folder, as the package scanner (Issue 5) would one day save it
        private void AddAPackageFinding()
        {
            using (ScannerDbContext context = _database.CreateContext())
            {
                context.Findings.Add(new Finding
                {
                    RepositoryId = context.Repositories.Single().Id,
                    Fingerprint = "package-finding",
                    ScannerType = ScannerType.Package,
                    RuleId = "PKG001",
                    Severity = Severity.High,
                    FilePath = "src/Api/Api.csproj",
                    Message = "Vulnerable package",
                    PackageName = "Newtonsoft.Json",
                    PackageVersion = "12.0.1",
                    AdvisoryUrl = "https://github.com/advisories/GHSA-5crp-9r3c-p9vr",
                    FirstSeenUtc = DateTime.UtcNow,
                    LastSeenUtc = DateTime.UtcNow
                });
                context.SaveChanges();
            }
        }

        // In the order they happened
        private static FindingEventType[] EventTypes(Finding finding)
        {
            return finding.Events.OrderBy(saved => saved.Id).Select(saved => saved.EventType).ToArray();
        }

        // A secret finding as SecretScanner would report it; the hash stands in for a real one
        private static RawFinding Raw(string ruleId, string filePath, int lineNumber, string secretHash)
        {
            return new RawFinding
            {
                ScannerType = ScannerType.Secret,
                RuleId = ruleId,
                Severity = Severity.High,
                FilePath = filePath,
                LineNumber = lineNumber,
                Message = "Test finding",
                MaskedPreview = "FAKE****",
                SecretHash = secretHash
            };
        }
    }
}
