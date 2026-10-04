using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using RepoScanner.Core.Entities;
using RepoScanner.Core.Enums;
using RepoScanner.Core.Persistence;
using RepoScanner.Core.Scanning;
using RepoScanner.Data;
using RepoScanner.Data.Repositories;
using RepoScanner.Scanner.Sources;
using RepoScanner.Scanner.Tests.Local;
using RepoScanner.Scanner.Tests.Logging;

namespace RepoScanner.Scanner.Tests
{
    [TestFixture]
    public class RepositoryScanRunnerTests
    {
        // The runner never reads this folder itself: the stub scanner stands in for the real one
        private static readonly string Folder = Path.Combine(Path.GetTempPath(), "orders-api");

        // A clone's commit ID, as git rev-parse HEAD gives it
        private const string CommitId = "27487b917ee49183c0c677159a43707cf20a9219";

        // The repository the scan is about, as a queue message would describe it
        private static readonly RepositoryDetails Repository = new RepositoryDetails(new Guid("0f8fad5b-d9cb-469f-a165-70867728950e"), "Lumina", "orders-api", "develop");

        // The message's correlation ID, given to every scan these tests run
        private static readonly Guid CorrelationId = new Guid("9a7b3c1d-2e4f-4a6b-8c0d-1e2f3a4b5c6d");

        private SqliteTestDatabase _database = null!;
        private FakeLogger<RepositoryScanRunner> _logger = null!;
        private FakeTimeProvider _time = null!;

        [SetUp]
        public void SetUp()
        {
            _database = new SqliteTestDatabase();
            _logger = new FakeLogger<RepositoryScanRunner>();
            _time = new FakeTimeProvider();
        }

        [TearDown]
        public void TearDown()
        {
            _database.Dispose();
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

        // ADR-013: a failed scan is always visible in ScanRuns. With its only scanner failed, the run is Failed,
        // and it keeps that scanner's result and says which scanner failed (#32)
        [Test]
        public async Task RunAsync_WhenItsOnlyScannerFails_MarksTheRunFailedAndRethrows()
        {
            await Assert.ThatAsync(() => RunAsync(new StubScanner(new InvalidOperationException("Simulated failure"))),
                Throws.InvalidOperationException);

            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun run = context.ScanRuns.Include(saved => saved.ScannerResults).Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(run.Status, Is.EqualTo(ScanRunStatus.Failed));
                    Assert.That(run.ErrorMessage, Is.EqualTo("Secret scanner failed: Simulated failure"));
                    Assert.That(run.CompletedUtc, Is.Not.Null);
                    Assert.That(run.ScannerResults.Select(result => result.Status), Is.EqualTo(new[] { ScannerStatus.Failed }));
                }
            }
        }

        // #32: some scanners failed and some succeeded
        [Test]
        public async Task RunAsync_WhenOneOfTwoScannersFails_MarksTheRunPartiallyCompletedSayingWhichFailed()
        {
            await RunAsync(new StubScanner(Raw("SEC003", "src/appsettings.json", 3, "hash-a")), FailingPackageScanner("nuget.org data could not be downloaded"));

            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun run = context.ScanRuns.Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(run.Status, Is.EqualTo(ScanRunStatus.PartiallyCompleted));
                    Assert.That(run.ErrorMessage, Is.EqualTo("Package scanner failed: nuget.org data could not be downloaded"));
                    Assert.That(run.NewCount, Is.EqualTo(1));
                    Assert.That(run.CompletedUtc, Is.Not.Null);
                }
            }
        }

        // #64: the run's last line says how it went, with everything a reader needs to act on it
        [Test]
        public async Task RunAsync_WhenItCompletes_EndsWithOneSummaryLineHoldingEveryCount()
        {
            await RunAsync(OpenClone, timeLimit: null, CancellationToken.None,
                new StubScanner(Raw("SEC003", "a.json", 3, "hash-a"), Raw("SEC007", "a.json", 6, "hash-b")), new StubScanner() { ScannerType = ScannerType.Package });

            FakeLogRecord summary = TheSummary();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(summary.Level, Is.EqualTo(LogLevel.Information));
                Assert.That(summary.GetStructuredStateValue("ScanRunStatus"), Is.EqualTo("Completed"));
                Assert.That(summary.GetStructuredStateValue("Trigger"), Is.EqualTo("OnDemand"));
                Assert.That(summary.GetStructuredStateValue("ProjectName"), Is.EqualTo("Lumina"));
                Assert.That(summary.GetStructuredStateValue("RepositoryName"), Is.EqualTo("orders-api"));
                Assert.That(summary.GetStructuredStateValue("CommitSha"), Is.EqualTo(CommitId));
                Assert.That(summary.GetStructuredStateValue("FoundCount"), Is.EqualTo("2"));
                Assert.That(summary.GetStructuredStateValue("NewCount"), Is.EqualTo("2"));
                Assert.That(summary.GetStructuredStateValue("ReopenedCount"), Is.EqualTo("0"));
                Assert.That(summary.GetStructuredStateValue("ResolvedCount"), Is.EqualTo("0"));
                Assert.That(summary.GetStructuredStateValue("SuppressionExpiredCount"), Is.EqualTo("0"));
                Assert.That(summary.GetStructuredStateValue("ScannerStatuses"), Is.EqualTo("[\"[Secret, Succeeded]\",\"[Package, Succeeded]\"]"));
                Assert.That(summary.Message, Does.Not.Contain("Error:"));
            }
        }

        // A partly failed run is not an error for the process: it is saved, and its summary is the warning that says why
        [Test]
        public async Task RunAsync_WhenPartiallyCompleted_TheSummaryIsAWarningWithTheError()
        {
            await RunAsync(new StubScanner(Raw("SEC003", "src/appsettings.json", 3, "hash-a")), FailingPackageScanner("nuget.org data could not be downloaded"));

            FakeLogRecord summary = TheSummary();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(summary.Level, Is.EqualTo(LogLevel.Warning));
                Assert.That(summary.GetStructuredStateValue("ScanRunStatus"), Is.EqualTo("PartiallyCompleted"));
                Assert.That(summary.GetStructuredStateValue("ErrorMessage"), Is.EqualTo("Package scanner failed: nuget.org data could not be downloaded"));
                Assert.That(summary.GetStructuredStateValue("ScannerStatuses"), Is.EqualTo("[\"[Secret, Succeeded]\",\"[Package, Failed]\"]"));
                Assert.That(_logger.Collector.GetSnapshot().Select(record => record.Message), Has.None.Contains("partially completed:"));
            }
        }

        // Every way a run fails ends with the same summary, so a search for failed scans finds them all (#64)
        [TestCase("every scanner fails")]
        [TestCase("the workspace cannot be opened")]
        [TestCase("the time limit is reached")]
        [TestCase("the run is cancelled")]
        [TestCase("saving the findings fails")]
        public async Task RunAsync_WhenTheRunFails_EndsWithOneFailedSummary(string how)
        {
            using (CancellationTokenSource stopping = new CancellationTokenSource())
            {
                Func<Task> run = how switch
                {
                    "every scanner fails" => () => RunAsync(
                        new StubScanner(new InvalidOperationException("Disk read failed")), FailingPackageScanner("nuget.org data could not be downloaded")),
                    "the workspace cannot be opened" => () => RunAsync(
                        _ => throw new DirectoryNotFoundException(@"No folder named Ghost in C:\repos"), timeLimit: null, CancellationToken.None, new StubScanner()),
                    "the time limit is reached" => () => RunAsync(
                        OpenFolder, TimeSpan.FromMinutes(15), CancellationToken.None, new StubScanner() { OnScan = () => _time.Advance(TimeSpan.FromMinutes(15)) }),
                    "the run is cancelled" => () => RunAsync(
                        stopping.Token, new StubScanner(new OperationCanceledException(stopping.Token)) { OnScan = stopping.Cancel }),
                    _ => () => RunAsync(new StubScanner(Raw("SEC003", "src/appsettings.json", 3, "hash-a") with { Message = null! }))
                };

                await Assert.ThatAsync(() => run(), Throws.InstanceOf<Exception>());
            }

            FakeLogRecord summary = TheSummary();
            string? savedError;
            using (ScannerDbContext context = _database.CreateContext())
            {
                savedError = context.ScanRuns.Single().ErrorMessage;
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(summary.Level, Is.EqualTo(LogLevel.Warning));
                Assert.That(summary.GetStructuredStateValue("ScanRunStatus"), Is.EqualTo("Failed"));
                Assert.That(new[] { "FoundCount", "NewCount", "ReopenedCount", "ResolvedCount" }.Select(name => summary.GetStructuredStateValue(name)),
                    Is.All.EqualTo("0"));
                Assert.That(savedError, Is.Not.Null);
                Assert.That(summary.GetStructuredStateValue("ErrorMessage"), Is.EqualTo(savedError));
                if (how == "the workspace cannot be opened")
                {
                    Assert.That(summary.GetStructuredStateValue("CommitSha"), Is.Null);
                    Assert.That(summary.GetStructuredStateValue("ScannerStatuses"), Is.EqualTo("[]"));
                }

                if (how == "every scanner fails")
                {
                    Assert.That(summary.GetStructuredStateValue("ScannerStatuses"), Is.EqualTo("[\"[Secret, Failed]\",\"[Package, Failed]\"]"));
                }
            }
        }

        // Issue 10 design, decision 7: the run's status is ScanRunStatus, so a search on it never mixes in anything else
        [Test]
        public async Task RunAsync_NoLineHasAStatusProperty()
        {
            await RunAsync(new StubScanner(Raw("SEC003", "src/appsettings.json", 3, "hash-a")));
            await RunAsync(new StubScanner(), FailingPackageScanner("nuget.org data could not be downloaded"));

            Assert.That(_logger.Collector.GetSnapshot().Select(record => record.GetStructuredStateValue("Status")), Is.All.Null);
        }

        // Issue 10 review: two scanners of one type is a misconfiguration the database already rejects (one result per
        // type). The summary must still be logged, and must not replace the database's error with an error of its own.
        [Test]
        public async Task RunAsync_WithTwoScannersOfOneType_LogsTheSummaryAndKeepsTheDatabasesError()
        {
            await Assert.ThatAsync(() => RunAsync(new StubScanner(), new StubScanner()), Throws.InstanceOf<DbUpdateException>());

            Assert.That(TheSummary().GetStructuredStateValue("ScanRunStatus"), Is.EqualTo("Failed"));
        }

        // Issue 10 review: DurationMs runs until the outcome is saved, as ScanRuns' times do, not on through deleting the
        // clone, which can take seconds on Windows
        [Test]
        public async Task RunAsync_TheSummarysDurationStopsWhenTheOutcomeIsSaved()
        {
            // The scan "takes" 2 minutes of fake time, and closing the clone 10 more
            StubScanner scanner = new StubScanner() { OnScan = () => _time.Advance(TimeSpan.FromMinutes(2)) };

            await RunAsync(
                _ => Task.FromResult(new RepositoryWorkspace(Folder, CommitId, () =>
                {
                    _time.Advance(TimeSpan.FromMinutes(10));
                    return ValueTask.CompletedTask;
                })),
                timeLimit: null,
                CancellationToken.None,
                scanner);

            Assert.That(TheSummary().GetStructuredStateValue("DurationMs"), Is.EqualTo("120000"));
        }

        // #32: with every scanner failed there is nothing to reconcile. The run is Failed, recorded once with every
        // scanner's result and each one's reason, and the first failure is rethrown, so the process exits with 1.
        [Test]
        public async Task RunAsync_WhenEveryScannerFails_MarksTheRunFailedWithEachScannersReason()
        {
            StubScanner secrets = new StubScanner(new InvalidOperationException("Disk read failed"));

            await Assert.ThatAsync(() => RunAsync(secrets, FailingPackageScanner("nuget.org data could not be downloaded")),
                Throws.InvalidOperationException.With.Message.EqualTo("Disk read failed"));

            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun run = context.ScanRuns.Include(saved => saved.ScannerResults).Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(run.Status, Is.EqualTo(ScanRunStatus.Failed));
                    Assert.That(run.ErrorMessage, Is.EqualTo("Secret scanner failed: Disk read failed; Package scanner failed: nuget.org data could not be downloaded"));
                    Assert.That(run.ScannerResults.Select(result => (result.ScannerType, result.Status)), Is.EquivalentTo(new[]
                    {
                        (ScannerType.Secret, ScannerStatus.Failed),
                        (ScannerType.Package, ScannerStatus.Failed)
                    }));
                }
            }
        }

        [Test]
        public async Task RunAsync_WhenEveryScannerSucceeds_MarksTheRunCompletedWithNoError()
        {
            await RunAsync(new StubScanner(Raw("SEC003", "src/appsettings.json", 3, "hash-a")), new StubScanner() { ScannerType = ScannerType.Package });

            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun run = context.ScanRuns.Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(run.Status, Is.EqualTo(ScanRunStatus.Completed));
                    Assert.That(run.ErrorMessage, Is.Null);
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

        // #63: one line per scanner in the same shape, so one search compares the three scanners' times and counts
        [Test]
        public async Task RunAsync_LogsOneLinePerScannerWithItsStatusTimeAndFindings()
        {
            await RunAsync(new StubScanner(Raw("SEC003", "a.json", 3, "hash-a"), Raw("SEC007", "a.json", 6, "hash-b")), new StubScanner() { ScannerType = ScannerType.Package });

            FakeLogRecord[] lines = _logger.Collector.GetSnapshot().Where(record => record.GetStructuredStateValue("ScannerStatus") != null).ToArray();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(lines.Select(line => line.GetStructuredStateValue("ScannerType")), Is.EqualTo(new[] { "Secret", "Package" }));
                Assert.That(lines.Select(line => line.GetStructuredStateValue("ScannerStatus")), Is.All.EqualTo("Succeeded"));
                Assert.That(lines.Select(line => line.GetStructuredStateValue("FindingCount")), Is.EqualTo(new[] { "2", "0" }));
                Assert.That(lines.Select(line => int.Parse(line.GetStructuredStateValue("ScannerDurationMs")!, CultureInfo.InvariantCulture)), Is.All.GreaterThanOrEqualTo(0));
                Assert.That(lines.Select(line => line.Level), Is.All.EqualTo(LogLevel.Information));
                Assert.That(lines[0].Message, Does.StartWith("Secret scanner Succeeded in ").And.EndWith(" ms: 2 findings"));
            }
        }

        // A failed scanner's line is the warning, with the exception, so there is one line per scanner however it did
        [Test]
        public async Task RunAsync_WhenAScannerFails_ItsLineIsAWarningWithTheException()
        {
            await RunAsync(new StubScanner(), FailingPackageScanner("nuget.org data could not be downloaded"));

            IReadOnlyList<FakeLogRecord> records = _logger.Collector.GetSnapshot();
            FakeLogRecord packages = records.Single(record => record.GetStructuredStateValue("ScannerType") == "Package");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(packages.Level, Is.EqualTo(LogLevel.Warning));
                Assert.That(packages.GetStructuredStateValue("ScannerStatus"), Is.EqualTo("Failed"));
                Assert.That(packages.GetStructuredStateValue("FindingCount"), Is.EqualTo("0"));
                Assert.That(packages.Exception, Is.InstanceOf<InvalidOperationException>());
                Assert.That(records.Select(record => record.Message), Has.None.Contains("the other scanners carry on"));
            }
        }

        // A scanner stopped part-way did not finish, so it has no time or count to give; the run's own line says why
        [Test]
        public async Task RunAsync_WhenTheRunIsCancelled_LogsNoLineForTheStoppedScanner()
        {
            using (CancellationTokenSource stopping = new CancellationTokenSource())
            {
                StubScanner cancelled = new StubScanner(new OperationCanceledException(stopping.Token)) { OnScan = stopping.Cancel };

                await Assert.ThatAsync(() => RunAsync(stopping.Token, cancelled), Throws.InstanceOf<OperationCanceledException>());
            }

            Assert.That(_logger.Collector.GetSnapshot().Where(record => record.GetStructuredStateValue("ScannerStatus") != null), Is.Empty);
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

        // Issue 10 design, decision 2: one search on a correlation ID or repository finds every line of the scan
        [Test]
        public async Task RunAsync_EveryLineItLogs_CarriesTheScansCorrelationIdAndRepository()
        {
            await RunAsync(new StubScanner(Raw("SEC003", "src/appsettings.json", 3, "hash-a")));

            IReadOnlyList<FakeLogRecord> records = _logger.Collector.GetSnapshot();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(records, Is.Not.Empty);
                Assert.That(records.Select(record => LogRecordValues.Scope(record, "CorrelationId")), Is.All.EqualTo(CorrelationId));
                Assert.That(records.Select(record => LogRecordValues.Scope(record, "ProjectName")), Is.All.EqualTo("Lumina"));
                Assert.That(records.Select(record => LogRecordValues.Scope(record, "RepositoryName")), Is.All.EqualTo("orders-api"));
            }
        }

        // The failure path too, since a failed scan is what someone searches the log for
        [Test]
        public async Task RunAsync_WhenAScannerFails_ItsWarningsCarryTheCorrelationId()
        {
            await RunAsync(new StubScanner(), FailingPackageScanner("nuget.org data could not be downloaded"));

            FakeLogRecord[] warnings = _logger.Collector.GetSnapshot().Where(record => record.Level == LogLevel.Warning).ToArray();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(warnings, Is.Not.Empty);
                Assert.That(warnings.Select(record => LogRecordValues.Scope(record, "CorrelationId")), Is.All.EqualTo(CorrelationId));
            }
        }

        [Test]
        public async Task RunAsync_ScansTheWorkspacesFolder()
        {
            StubScanner scanner = new StubScanner();

            await RunAsync(scanner);

            Assert.That(scanner.RootPath, Is.EqualTo(Folder));
        }

        // Issue 7 design, decision 8: the folder is opened after the run starts, so a folder that cannot be
        // opened (from Issue 8, a clone that fails) is recorded against that run
        [Test]
        public async Task RunAsync_OpensTheWorkspaceOnceTheRunHasStarted()
        {
            ScanRunStatus? statusWhenOpened = null;

            await RunAsync(_ =>
            {
                using (ScannerDbContext context = _database.CreateContext())
                {
                    statusWhenOpened = context.ScanRuns.Single().Status;
                }

                return Task.FromResult(RepositoryWorkspace.ForExistingFolder(Folder));
            }, timeLimit: null, CancellationToken.None, new StubScanner());

            Assert.That(statusWhenOpened, Is.EqualTo(ScanRunStatus.Running));
        }

        [Test]
        public async Task RunAsync_WhenTheWorkspaceCannotBeOpened_MarksTheRunFailedWithTheReasonAndRethrows()
        {
            StubScanner scanner = new StubScanner();

            await Assert.ThatAsync(
                () => RunAsync(_ => throw new DirectoryNotFoundException(@"No folder named Ghost in C:\repos"), timeLimit: null, CancellationToken.None, scanner),
                Throws.InstanceOf<DirectoryNotFoundException>());

            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun run = context.ScanRuns.Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(run.Status, Is.EqualTo(ScanRunStatus.Failed));
                    Assert.That(run.ErrorMessage, Is.EqualTo(@"No folder named Ghost in C:\repos"));

                    // A clone that failed has no commit ID (ScanRunCompletion: "Empty when the clone failed")
                    Assert.That(run.CommitSha, Is.Null);
                    Assert.That(scanner.CallCount, Is.Zero);
                }
            }
        }

        // From Issue 8 closing the workspace deletes the clone, which must happen whatever the scan did
        [Test]
        public async Task RunAsync_ClosesTheWorkspaceAfterTheScan()
        {
            int closed = 0;

            await RunAsync(_ => Task.FromResult(WorkspaceThatCounts(() => closed++)), timeLimit: null, CancellationToken.None, new StubScanner());

            Assert.That(closed, Is.EqualTo(1));
        }

        [Test]
        public async Task RunAsync_WhenTheScanFails_StillClosesTheWorkspace()
        {
            int closed = 0;

            await Assert.ThatAsync(
                () => RunAsync(_ => Task.FromResult(WorkspaceThatCounts(() => closed++)), timeLimit: null, CancellationToken.None,
                    new StubScanner(new InvalidOperationException("Simulated failure"))),
                Throws.InvalidOperationException);

            Assert.That(closed, Is.EqualTo(1));
        }

        // Issue 7 design: a repository that takes longer than ScanTimeoutPerRepoMinutes is stopped, and its run
        // says why. A TimeoutException rather than a cancellation, so the loop treats it as a failed scan and
        // carries on, rather than as the job being stopped.
        [Test]
        public async Task RunAsync_WhenTheTimeLimitIsReached_MarksTheRunFailedSayingSoAndThrowsATimeout()
        {
            // The scan "takes" 15 minutes of fake time
            StubScanner slow = new StubScanner(Raw("SEC003", "src/appsettings.json", 3, "hash-a")) { OnScan = () => _time.Advance(TimeSpan.FromMinutes(15)) };
            StubScanner after = new StubScanner() { ScannerType = ScannerType.Package };

            await Assert.ThatAsync(() => RunAsync(OpenFolder, TimeSpan.FromMinutes(15), CancellationToken.None, slow, after), Throws.TypeOf<TimeoutException>());

            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun run = context.ScanRuns.Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(run.Status, Is.EqualTo(ScanRunStatus.Failed));
                    Assert.That(run.ErrorMessage, Is.EqualTo("The scan took longer than 15 minutes and was stopped (Scanner:Execution:ScanTimeoutPerRepoMinutes)"));
                    Assert.That(after.CallCount, Is.Zero);
                    Assert.That(context.Findings.Count(), Is.Zero);
                }
            }
        }

        [Test]
        public async Task RunAsync_WithinTheTimeLimit_Completes()
        {
            StubScanner scanner = new StubScanner() { OnScan = () => _time.Advance(TimeSpan.FromMinutes(14)) };

            await RunAsync(OpenFolder, TimeSpan.FromMinutes(15), CancellationToken.None, scanner);

            using (ScannerDbContext context = _database.CreateContext())
            {
                Assert.That(context.ScanRuns.Single().Status, Is.EqualTo(ScanRunStatus.Completed));
            }
        }

        // The job being stopped is still a cancellation when there is a time limit too
        [Test]
        public async Task RunAsync_StoppedBeforeTheTimeLimit_IsACancellationNotATimeout()
        {
            using (CancellationTokenSource stopping = new CancellationTokenSource())
            {
                StubScanner cancelled = new StubScanner(new OperationCanceledException(stopping.Token)) { OnScan = stopping.Cancel };

                await Assert.ThatAsync(() => RunAsync(OpenFolder, TimeSpan.FromMinutes(15), stopping.Token, cancelled),
                    Throws.InstanceOf<OperationCanceledException>());
            }
        }

        // Issue 8 design, The commit ID: once the clone has opened, every way the run ends saves its commit ID
        [Test]
        public async Task RunAsync_SavesTheWorkspacesCommitId()
        {
            await RunAsync(OpenClone, timeLimit: null, CancellationToken.None, new StubScanner(Raw("SEC003", "src/appsettings.json", 3, "hash-a")));

            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun run = context.ScanRuns.Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(run.Status, Is.EqualTo(ScanRunStatus.Completed));
                    Assert.That(run.CommitSha, Is.EqualTo(CommitId));
                }
            }
        }

        [Test]
        public async Task RunAsync_WhenSomeScannersFail_StillSavesTheCommitId()
        {
            await RunAsync(OpenClone, timeLimit: null, CancellationToken.None,
                new StubScanner(Raw("SEC003", "src/appsettings.json", 3, "hash-a")), FailingPackageScanner("nuget.org data could not be downloaded"));

            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun run = context.ScanRuns.Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(run.Status, Is.EqualTo(ScanRunStatus.PartiallyCompleted));
                    Assert.That(run.CommitSha, Is.EqualTo(CommitId));
                }
            }
        }

        // The clone worked, so the run says which commit the scanners failed on
        [Test]
        public async Task RunAsync_WhenEveryScannerFails_SavesTheCommitId()
        {
            await Assert.ThatAsync(
                () => RunAsync(OpenClone, timeLimit: null, CancellationToken.None, new StubScanner(new InvalidOperationException("Simulated failure"))),
                Throws.InvalidOperationException);

            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun run = context.ScanRuns.Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(run.Status, Is.EqualTo(ScanRunStatus.Failed));
                    Assert.That(run.CommitSha, Is.EqualTo(CommitId));
                }
            }
        }

        [Test]
        public async Task RunAsync_WhenTheTimeLimitIsReached_SavesTheCommitId()
        {
            StubScanner slow = new StubScanner() { OnScan = () => _time.Advance(TimeSpan.FromMinutes(15)) };

            await Assert.ThatAsync(() => RunAsync(OpenClone, TimeSpan.FromMinutes(15), CancellationToken.None, slow), Throws.TypeOf<TimeoutException>());

            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun run = context.ScanRuns.Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(run.ErrorMessage, Does.StartWith("The scan took longer than 15 minutes"));
                    Assert.That(run.CommitSha, Is.EqualTo(CommitId));
                }
            }
        }

        // The job being stopped after the clone is recorded by the other catch, which must save it too
        [Test]
        public async Task RunAsync_WhenTheRunIsCancelledAfterTheClone_SavesTheCommitId()
        {
            using (CancellationTokenSource stopping = new CancellationTokenSource())
            {
                StubScanner cancelled = new StubScanner(new OperationCanceledException(stopping.Token)) { OnScan = stopping.Cancel };

                await Assert.ThatAsync(() => RunAsync(OpenClone, timeLimit: null, stopping.Token, cancelled), Throws.InstanceOf<OperationCanceledException>());
            }

            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun run = context.ScanRuns.Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(run.Status, Is.EqualTo(ScanRunStatus.Failed));
                    Assert.That(run.CommitSha, Is.EqualTo(CommitId));
                }
            }
        }

        // The one summary line of the run (#64): there is exactly one, and it is the last line the runner logs. FakeLogger
        // keeps its ScannerStatuses dictionary as text, one "[type, status]" per scanner; the JSON object Serilog writes
        // from it is checked through the whole program (#100).
        private FakeLogRecord TheSummary()
        {
            IReadOnlyList<FakeLogRecord> records = _logger.Collector.GetSnapshot();
            FakeLogRecord[] summaries = records.Where(record => record.Message.StartsWith("Scan run ", StringComparison.Ordinal)).ToArray();

            Assert.That(summaries, Has.Length.EqualTo(1), "one summary line");
            Assert.That(records[records.Count - 1], Is.SameAs(summaries[0]), "the summary is logged last");
            return summaries[0];
        }

        private Task RunAsync(params IScanner[] scanners)
        {
            return RunAsync(CancellationToken.None, scanners);
        }

        private Task RunAsync(CancellationToken cancellationToken, params IScanner[] scanners)
        {
            return RunAsync(OpenFolder, timeLimit: null, cancellationToken, scanners);
        }

        private async Task RunAsync(
            Func<CancellationToken, Task<RepositoryWorkspace>> openWorkspace, TimeSpan? timeLimit, CancellationToken cancellationToken, params IScanner[] scanners)
        {
            using (ScannerDbContext context = _database.CreateContext())
            {
                RepositoryScanRunner runner = new RepositoryScanRunner(new ScanRunRepository(context), new FindingRepository(context), scanners, _time, _logger);

                await runner.RunAsync(Repository, ScanTrigger.OnDemand, CorrelationId, openWorkspace, timeLimit, cancellationToken);
            }
        }

        private static Task<RepositoryWorkspace> OpenFolder(CancellationToken cancellationToken)
        {
            return Task.FromResult(RepositoryWorkspace.ForExistingFolder(Folder));
        }

        // A cloned workspace, as AzureDevOpsCloneSource opens it, with its commit ID
        private static Task<RepositoryWorkspace> OpenClone(CancellationToken cancellationToken)
        {
            return Task.FromResult(new RepositoryWorkspace(Folder, CommitId, () => ValueTask.CompletedTask));
        }

        // A workspace whose clean-up only counts, as Issue 8's will delete the clone
        private static RepositoryWorkspace WorkspaceThatCounts(Action onClose)
        {
            return new RepositoryWorkspace(Folder, commitSha: null, () =>
            {
                onClose();
                return ValueTask.CompletedTask;
            });
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
