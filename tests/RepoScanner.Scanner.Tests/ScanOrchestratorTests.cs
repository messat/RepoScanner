using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using RepoScanner.Core.Entities;
using RepoScanner.Core.Enums;
using RepoScanner.Core.Persistence;
using RepoScanner.Core.Scanning;
using RepoScanner.Data;
using RepoScanner.Data.Repositories;
using RepoScanner.Scanner.Configuration;
using RepoScanner.Scanner.Queue;
using RepoScanner.Scanner.Sources;
using RepoScanner.Scanner.Tests.Local;
using RepoScanner.Scanner.Tests.Logging;
using RepoScanner.Scanner.Tests.Queue;
using RepoScanner.Scanner.Tests.Sources;

namespace RepoScanner.Scanner.Tests
{
    // The queue loop (#31) with the queue in memory, the repositories as folders in a temporary root, a stub scanner and
    // an in-memory database: everything real except the queue, the source, the scanner and the clock. Cloning has its
    // own tests (AzureDevOpsCloneSourceTests).
    [TestFixture]
    public class ScanOrchestratorTests
    {
        private static readonly Guid OrdersApiId = new Guid("0f8fad5b-d9cb-469f-a165-70867728950e");

        private SqliteTestDatabase _database = null!;
        private TempFolder _root = null!;
        private FakeRepositorySource _source = null!;
        private FakeScanQueue _queue = null!;
        private FakeTimeProvider _time = null!;
        private FakeLogger<ScanOrchestrator> _logger = null!;
        private ServiceProvider? _services;

        [SetUp]
        public void SetUp()
        {
            _database = new SqliteTestDatabase();
            _root = new TempFolder();
            _source = new FakeRepositorySource(_root.FullPath);
            _queue = new FakeScanQueue();

            // Starts at a real date, so the ages of seeded Running runs read like the real thing
            _time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 2, 0, 0, TimeSpan.Zero));
            _logger = new FakeLogger<ScanOrchestrator>();
        }

        [TearDown]
        public void TearDown()
        {
            _services?.Dispose();
            _root.Dispose();
            _database.Dispose();
        }

        [Test]
        public async Task RunAsync_WhenStarting_LogsEnvironmentAndQueueAsStructuredProperties()
        {
            await CreateOrchestrator(new StubScanner()).RunAsync(CancellationToken.None);

            FakeLogRecord first = _logger.Collector.GetSnapshot()[0];
            using (Assert.EnterMultipleScope())
            {
                Assert.That(first.GetStructuredStateValue("EnvironmentName"), Is.EqualTo("Test"));
                Assert.That(first.GetStructuredStateValue("QueueName"), Is.EqualTo("scan-nightly"));
            }
        }

        // Issue 7 success criterion 2: an empty queue ends the run at once
        [Test]
        public async Task RunAsync_WithAnEmptyQueue_StopsSayingSo()
        {
            await CreateOrchestrator(new StubScanner()).RunAsync(CancellationToken.None);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_queue.VisibilityTimeouts, Is.EqualTo(new[] { TimeSpan.FromMinutes(65) }));
                Assert.That(_logger.LatestRecord.GetStructuredStateValue("Reason"), Is.EqualTo("queue empty"));
            }
        }

        // Issue 7 success criterion 1: one message gives one scan run with findings, saved under the message's
        // repository, and the message is deleted
        [Test]
        public async Task RunAsync_WithOneMessage_ScansItSavesItAndDeletesTheMessage()
        {
            CreateRepositoryFolder("orders-api");
            Guid correlationId = new Guid("5b1c6f3e-8a2d-4e7f-9c1a-2d3e4f5a6b7c");
            Guid repositoryId = new Guid("0f8fad5b-d9cb-469f-a165-70867728950e");
            ScanQueueMessage message = _queue.Add(Body("orders-api", repositoryId, correlationId));
            StubScanner scanner = new StubScanner(Raw("SEC003", "src/appsettings.json", 3));

            await CreateOrchestrator(scanner).RunAsync(CancellationToken.None);

            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun run = context.ScanRuns.Include(saved => saved.Repository).Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(run.Status, Is.EqualTo(ScanRunStatus.Completed));
                    Assert.That(run.TriggerType, Is.EqualTo(ScanTrigger.Nightly));
                    Assert.That(run.CorrelationId, Is.EqualTo(correlationId));
                    Assert.That(run.NewCount, Is.EqualTo(1));
                    Assert.That(run.Repository.AzureDevOpsId, Is.EqualTo(repositoryId));
                    Assert.That(run.Repository.ProjectName, Is.EqualTo("Lumina"));
                    Assert.That(run.Repository.Name, Is.EqualTo("orders-api"));
                    Assert.That(run.Repository.DefaultBranch, Is.EqualTo("develop"));
                    Assert.That(context.Findings.Count(), Is.EqualTo(1));
                    Assert.That(scanner.RootPath, Is.EqualTo(Path.Combine(_root.FullPath, "orders-api")));
                    Assert.That(_queue.Deleted, Is.EqualTo(new[] { message }));
                }
            }
        }

        // Issue 7 design, decision 5: the queue, not the message, says which lane a scan belongs to
        [Test]
        public async Task RunAsync_FromTheOnDemandQueue_SavesAnOnDemandRun()
        {
            CreateRepositoryFolder("orders-api");
            _queue.Add(Body("orders-api"));

            await CreateOrchestrator(new StubScanner(), queueName: "scan-ondemand").RunAsync(CancellationToken.None);

            using (ScannerDbContext context = _database.CreateContext())
            {
                Assert.That(context.ScanRuns.Single().TriggerType, Is.EqualTo(ScanTrigger.OnDemand));
            }
        }

        // One after another, never two at once (decision 10)
        [Test]
        public async Task RunAsync_WithTenMessages_ScansThemInTurnAndDeletesEach()
        {
            string[] names = Enumerable.Range(0, 10).Select(number => "repo-" + number).ToArray();
            foreach (string name in names)
            {
                CreateRepositoryFolder(name);
                _queue.Add(Body(name));
            }

            await CreateOrchestrator(new StubScanner()).RunAsync(CancellationToken.None);

            using (ScannerDbContext context = _database.CreateContext())
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(context.ScanRuns.OrderBy(run => run.Id).Select(run => run.Repository.Name), Is.EqualTo(names));
                    Assert.That(_queue.Deleted, Has.Count.EqualTo(10));
                }
            }
        }

        // Decision 12: a failed scan leaves its message, to reappear and be tried again; the next message is scanned
        [Test]
        public async Task RunAsync_WhenAScanFails_LeavesItsMessageAndCarriesOn()
        {
            ScanQueueMessage ghost = _queue.Add(Body("Ghost"));
            CreateRepositoryFolder("orders-api");
            ScanQueueMessage orders = _queue.Add(Body("orders-api"));

            await CreateOrchestrator(new StubScanner()).RunAsync(CancellationToken.None);

            using (ScannerDbContext context = _database.CreateContext())
            {
                Dictionary<string, ScanRunStatus> statuses = context.ScanRuns.Include(run => run.Repository).ToDictionary(run => run.Repository.Name, run => run.Status);
                FakeLogRecord warning = _logger.Collector.GetSnapshot().Single(record => record.Level == LogLevel.Warning);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(statuses["Ghost"], Is.EqualTo(ScanRunStatus.Failed));
                    Assert.That(statuses["orders-api"], Is.EqualTo(ScanRunStatus.Completed));
                    Assert.That(_queue.Deleted, Is.EqualTo(new[] { orders }));
                    Assert.That(_queue.Poisoned, Is.Empty);
                    Assert.That(warning.GetStructuredStateValue("MessageId"), Is.EqualTo(ghost.MessageId));
                    Assert.That(warning.Exception, Is.InstanceOf<DirectoryNotFoundException>());
                }
            }
        }

        // Issue 7 success criterion 3 and decision 15: straight to the lane's poison queue, with no scan run
        [Test]
        public async Task RunAsync_WithAMalformedMessage_PoisonsItWithoutAScanRun()
        {
            ScanQueueMessage malformed = _queue.Add("{ \"hello\": 1 }");
            StubScanner scanner = new StubScanner();

            await CreateOrchestrator(scanner).RunAsync(CancellationToken.None);

            FakeLogRecord warning = _logger.Collector.GetSnapshot().Single(record => record.Level == LogLevel.Warning);
            using (ScannerDbContext context = _database.CreateContext())
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_queue.Poisoned, Is.EqualTo(new[] { malformed }));
                Assert.That(_queue.Deleted, Is.Empty);
                Assert.That(context.ScanRuns.Count(), Is.Zero);
                Assert.That(scanner.CallCount, Is.Zero);
                Assert.That(warning.GetStructuredStateValue("PoisonQueueName"), Is.EqualTo("scan-nightly-poison"));
                Assert.That(warning.GetStructuredStateValue("Reason"), Is.EqualTo("schemaVersion is missing"));
            }
        }

        // ADR-013: tried three times, so the fourth pick-up poisons it without scanning
        [Test]
        public async Task RunAsync_OnTheFourthPickUp_PoisonsItWithoutScanning()
        {
            CreateRepositoryFolder("orders-api");
            ScanQueueMessage message = _queue.Add(Body("orders-api"), dequeueCount: 4);
            StubScanner scanner = new StubScanner();

            await CreateOrchestrator(scanner).RunAsync(CancellationToken.None);

            FakeLogRecord warning = _logger.Collector.GetSnapshot().Single(record => record.Level == LogLevel.Warning);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_queue.Poisoned, Is.EqualTo(new[] { message }));
                Assert.That(scanner.CallCount, Is.Zero);
                Assert.That(warning.GetStructuredStateValue("Reason"), Is.EqualTo("tried 3 times without success"));
                Assert.That(warning.GetStructuredStateValue("RepositoryName"), Is.EqualTo("orders-api"));
            }
        }

        [Test]
        public async Task RunAsync_OnTheThirdPickUp_StillScansIt()
        {
            CreateRepositoryFolder("orders-api");
            ScanQueueMessage message = _queue.Add(Body("orders-api"), dequeueCount: 3);

            await CreateOrchestrator(new StubScanner()).RunAsync(CancellationToken.None);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_queue.Deleted, Is.EqualTo(new[] { message }));
                Assert.That(_queue.Poisoned, Is.Empty);
            }
        }

        // The worked example in the Issue 7 design: at 6 minutes a repository, six start by minute 30; at minute
        // 36 only 14 of the 50 are left, less than a repository's 15, so the rest wait for the next run
        [Test]
        public async Task RunAsync_WithLessThanAScanTimeoutLeft_StartsNoMoreMessages()
        {
            for (int number = 0; number < 10; number++)
            {
                CreateRepositoryFolder("repo-" + number);
                _queue.Add(Body("repo-" + number));
            }

            StubScanner sixMinutes = new StubScanner() { OnScan = () => _time.Advance(TimeSpan.FromMinutes(6)) };

            await CreateOrchestrator(sixMinutes).RunAsync(CancellationToken.None);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_queue.Deleted, Has.Count.EqualTo(6));
                Assert.That(_queue.WaitingCount, Is.EqualTo(4));
                Assert.That(_logger.LatestRecord.GetStructuredStateValue("Reason"), Is.EqualTo("time budget spent"));
            }
        }

        // The boundary: exactly a repository's time left is enough to start one more
        [Test]
        public async Task RunAsync_WithExactlyAScanTimeoutLeft_StartsOneMore()
        {
            for (int number = 0; number < 3; number++)
            {
                CreateRepositoryFolder("repo-" + number);
                _queue.Add(Body("repo-" + number));
            }

            StubScanner fiveMinutes = new StubScanner() { OnScan = () => _time.Advance(TimeSpan.FromMinutes(5)) };
            ExecutionOptions execution = new ExecutionOptions { MaxRunDurationMinutes = 20, ScanTimeoutPerRepoMinutes = 15 };

            await CreateOrchestrator(fiveMinutes, execution: execution).RunAsync(CancellationToken.None);

            Assert.That(_queue.Deleted, Has.Count.EqualTo(2));
        }

        // A repository over its time limit is a failed scan: recorded, its message left, and the loop carries on
        [Test]
        public async Task RunAsync_WhenAScanTakesTooLong_RecordsWhyAndCarriesOn()
        {
            CreateRepositoryFolder("orders-api");
            _queue.Add(Body("orders-api"));
            StubScanner tooSlow = new StubScanner() { OnScan = () => _time.Advance(TimeSpan.FromMinutes(15)) };

            await CreateOrchestrator(tooSlow).RunAsync(CancellationToken.None);

            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun run = context.ScanRuns.Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(run.Status, Is.EqualTo(ScanRunStatus.Failed));
                    Assert.That(run.ErrorMessage, Does.StartWith("The scan took longer than 15 minutes"));
                    Assert.That(_queue.Deleted, Is.Empty);
                    Assert.That(_logger.LatestRecord.GetStructuredStateValue("Reason"), Is.EqualTo("queue empty"));
                }
            }
        }

        // Ctrl+C locally, SIGTERM in Azure: the current scan is marked Failed, its message left, and the run ends
        [Test]
        public async Task RunAsync_WhenTheJobIsStopped_LeavesTheMessageAndStops()
        {
            CreateRepositoryFolder("orders-api");
            CreateRepositoryFolder("billing");
            _queue.Add(Body("orders-api"));
            _queue.Add(Body("billing"));

            using (CancellationTokenSource stopping = new CancellationTokenSource())
            {
                StubScanner stopped = new StubScanner(new OperationCanceledException(stopping.Token)) { OnScan = stopping.Cancel };

                await Assert.ThatAsync(() => CreateOrchestrator(stopped).RunAsync(stopping.Token), Throws.InstanceOf<OperationCanceledException>());
            }

            using (ScannerDbContext context = _database.CreateContext())
            using (Assert.EnterMultipleScope())
            {
                Assert.That(context.ScanRuns.Single().Status, Is.EqualTo(ScanRunStatus.Failed));
                Assert.That(_queue.Deleted, Is.Empty);
                Assert.That(_queue.WaitingCount, Is.EqualTo(1));
                Assert.That(_logger.LatestRecord.GetStructuredStateValue("Reason"), Is.EqualTo("asked to stop"));
            }
        }

        // Issue 7 success criterion 4 and ADR-027: another run is scanning this repository, so the message is deleted
        // and logged as skipped. 20 minutes (ScanTimeoutPerRepoMinutes + 5) is still live.
        [Test]
        public async Task RunAsync_WhenTheRepositoryIsBeingScanned_DeletesTheMessageAndLogsSkipped()
        {
            CreateRepositoryFolder("orders-api");
            int running = SeedRunningRun(OrdersApiId, startedAgo: TimeSpan.FromMinutes(20));
            ScanQueueMessage message = _queue.Add(Body("orders-api", OrdersApiId));
            StubScanner scanner = new StubScanner();

            await CreateOrchestrator(scanner).RunAsync(CancellationToken.None);

            FakeLogRecord skipped = _logger.Collector.GetSnapshot().Single(record => record.Message.StartsWith("Skipped", StringComparison.Ordinal));
            using (ScannerDbContext context = _database.CreateContext())
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_queue.Deleted, Is.EqualTo(new[] { message }));
                Assert.That(context.ScanRuns.Count(), Is.EqualTo(1), "no new scan run");
                Assert.That(context.ScanRuns.Single().Status, Is.EqualTo(ScanRunStatus.Running));
                Assert.That(scanner.CallCount, Is.Zero);
                Assert.That(skipped.GetStructuredStateValue("ScanRunId"), Is.EqualTo(running.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                Assert.That(_logger.LatestRecord.GetStructuredStateValue("SkippedCount"), Is.EqualTo("1"));
            }
        }

        // Issue 7 success criterion 5 and ADR-027: a live scan is stopped at 15 minutes, so a run Running for more
        // than 20 must belong to a run that died. It is marked Failed, and the repository is scanned.
        [Test]
        public async Task RunAsync_WhenTheRunningScanIsOlderThanTheLimitPlusFive_TakesItOver()
        {
            CreateRepositoryFolder("orders-api");
            int abandoned = SeedRunningRun(OrdersApiId, startedAgo: TimeSpan.FromMinutes(21));
            ScanQueueMessage message = _queue.Add(Body("orders-api", OrdersApiId));
            StubScanner scanner = new StubScanner();

            await CreateOrchestrator(scanner).RunAsync(CancellationToken.None);

            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun old = context.ScanRuns.Single(run => run.Id == abandoned);
                ScanRun taken = context.ScanRuns.Single(run => run.Id != abandoned);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(old.Status, Is.EqualTo(ScanRunStatus.Failed));
                    Assert.That(old.ErrorMessage, Is.EqualTo("Abandoned: still Running more than 20 minutes after it started, so the run that started it had stopped"));
                    Assert.That(old.CompletedUtc, Is.Not.Null);
                    Assert.That(taken.Status, Is.EqualTo(ScanRunStatus.Completed));
                    Assert.That(scanner.CallCount, Is.EqualTo(1));
                    Assert.That(_queue.Deleted, Is.EqualTo(new[] { message }));
                }
            }
        }

        // The take-over is said in the log too, not only in ScanRuns, so it shows in the console and Log Analytics
        [Test]
        public async Task RunAsync_WhenTakingOverAnAbandonedRun_LogsAWarningNamingIt()
        {
            CreateRepositoryFolder("orders-api");
            int abandoned = SeedRunningRun(OrdersApiId, startedAgo: TimeSpan.FromMinutes(30));
            _queue.Add(Body("orders-api", OrdersApiId));

            await CreateOrchestrator(new StubScanner()).RunAsync(CancellationToken.None);

            FakeLogRecord warning = _logger.Collector.GetSnapshot().Single(record => record.Level == LogLevel.Warning);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(warning.GetStructuredStateValue("ScanRunId"), Is.EqualTo(abandoned.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                Assert.That(warning.GetStructuredStateValue("RepositoryName"), Is.EqualTo("orders-api"));
                Assert.That(warning.GetStructuredStateValue("MinutesRunning"), Is.EqualTo("30"));
                Assert.That(warning.Message, Does.Contain("abandoned").And.Contain("scanning again"));
            }
        }

        // One live run is enough to skip, even beside an abandoned one
        [Test]
        public async Task RunAsync_WithAnAbandonedAndALiveRunningScan_Skips()
        {
            CreateRepositoryFolder("orders-api");
            SeedRunningRun(OrdersApiId, startedAgo: TimeSpan.FromMinutes(70));
            SeedRunningRun(OrdersApiId, startedAgo: TimeSpan.FromMinutes(5));
            _queue.Add(Body("orders-api", OrdersApiId));
            StubScanner scanner = new StubScanner();

            await CreateOrchestrator(scanner).RunAsync(CancellationToken.None);

            Assert.That(scanner.CallCount, Is.Zero);
        }

        // Another repository being scanned does not matter
        [Test]
        public async Task RunAsync_WhenAnotherRepositoryIsBeingScanned_ScansThisOne()
        {
            CreateRepositoryFolder("orders-api");
            SeedRunningRun(Guid.NewGuid(), startedAgo: TimeSpan.FromMinutes(1));
            _queue.Add(Body("orders-api", OrdersApiId));
            StubScanner scanner = new StubScanner();

            await CreateOrchestrator(scanner).RunAsync(CancellationToken.None);

            Assert.That(scanner.CallCount, Is.EqualTo(1));
        }

        // Issue 7 design, Poison: a crash leaves its run Running, so on the 4th pick-up that run is marked Failed; a
        // young Running run belongs to another live run and is left alone
        [Test]
        public async Task RunAsync_OnTheFourthPickUp_MarksACrashedRunFailedButLeavesALiveOne()
        {
            CreateRepositoryFolder("orders-api");
            int crashed = SeedRunningRun(OrdersApiId, startedAgo: TimeSpan.FromMinutes(70));
            int live = SeedRunningRun(OrdersApiId, startedAgo: TimeSpan.FromMinutes(5));
            ScanQueueMessage message = _queue.Add(Body("orders-api", OrdersApiId), dequeueCount: 4);

            await CreateOrchestrator(new StubScanner()).RunAsync(CancellationToken.None);

            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun crashedRun = context.ScanRuns.Single(run => run.Id == crashed);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(_queue.Poisoned, Is.EqualTo(new[] { message }));
                    Assert.That(crashedRun.Status, Is.EqualTo(ScanRunStatus.Failed));
                    Assert.That(crashedRun.ErrorMessage, Is.EqualTo("Moved to scan-nightly-poison after 3 attempts"));
                    Assert.That(context.ScanRuns.Single(run => run.Id == live).Status, Is.EqualTo(ScanRunStatus.Running));
                }
            }
        }

        // Issue 7 design, The loop: with the database out of reach every message would fail the same way, so the run
        // stops (exit 1) and the message is left to reappear
        [Test]
        public async Task RunAsync_WhenTheDatabaseCannotBeReached_StopsAndLeavesTheMessage()
        {
            CreateRepositoryFolder("orders-api");
            _queue.Add(Body("orders-api", OrdersApiId));

            ScanOrchestrator orchestrator = CreateOrchestrator(
                new StubScanner(), configure: services => services.AddScoped<IScanRunRepository, UnreachableScanRunRepository>());

            await Assert.ThatAsync(() => orchestrator.RunAsync(CancellationToken.None), Throws.InvalidOperationException);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_queue.Deleted, Is.Empty);
                Assert.That(_queue.Poisoned, Is.Empty);
            }
        }

        // Issue 13 criterion 1: the first copy is scanned. Its run started after both copies were queued, so the second is
        // already done. The scan run's start comes from the real clock, days after the fake one, so after InsertedOn.
        [Test]
        public async Task RunAsync_WithTwoCopiesOfOneRequest_ScansOnceAndDeletesBoth()
        {
            CreateRepositoryFolder("orders-api");
            string body = Body("orders-api", OrdersApiId);
            ScanQueueMessage first = _queue.Add(body, insertedOn: QueuedAgo(TimeSpan.FromMinutes(1)));
            ScanQueueMessage second = _queue.Add(body, insertedOn: QueuedAgo(TimeSpan.FromMinutes(1)));
            StubScanner scanner = new StubScanner();

            await CreateOrchestrator(scanner).RunAsync(CancellationToken.None);

            using (ScannerDbContext context = _database.CreateContext())
            using (Assert.EnterMultipleScope())
            {
                Assert.That(context.ScanRuns.Count(), Is.EqualTo(1));
                Assert.That(scanner.CallCount, Is.EqualTo(1));
                Assert.That(_queue.Deleted, Is.EqualTo(new[] { first, second }));
                Assert.That(_logger.LatestRecord.GetStructuredStateValue("ScannedCount"), Is.EqualTo("1"));
                Assert.That(_logger.LatestRecord.GetStructuredStateValue("AlreadyDoneCount"), Is.EqualTo("1"));
            }
        }

        // Issue 13: a good scan of this lane that started after the message was queued has done what it asks
        [TestCase(ScanRunStatus.Completed)]
        [TestCase(ScanRunStatus.PartiallyCompleted)]
        public async Task RunAsync_WhenAGoodScanStartedAfterTheMessageWasQueued_DeletesItWithoutScanning(ScanRunStatus status)
        {
            CreateRepositoryFolder("orders-api");
            int done = SeedRun(OrdersApiId, status, ScanTrigger.Nightly, startedAgo: TimeSpan.FromMinutes(5));
            Guid correlationId = new Guid("8e1f3a5c-7b9d-4c2e-a6f8-1d3b5c7e9f0a");
            ScanQueueMessage message = _queue.Add(Body("orders-api", OrdersApiId, correlationId), insertedOn: QueuedAgo(TimeSpan.FromMinutes(10)));
            StubScanner scanner = new StubScanner();

            await CreateOrchestrator(scanner).RunAsync(CancellationToken.None);

            FakeLogRecord line = AlreadyDoneLine();
            using (ScannerDbContext context = _database.CreateContext())
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_queue.Deleted, Is.EqualTo(new[] { message }));
                Assert.That(context.ScanRuns.Count(), Is.EqualTo(1), "no new scan run");
                Assert.That(scanner.CallCount, Is.Zero);
                Assert.That(line.Level, Is.EqualTo(LogLevel.Information));
                Assert.That(line.GetStructuredStateValue("ScanRunId"), Is.EqualTo(done.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                Assert.That(line.GetStructuredStateValue("RepositoryName"), Is.EqualTo("orders-api"));
                Assert.That(LogRecordValues.Scope(line, "CorrelationId"), Is.EqualTo(correlationId));
            }
        }

        // Issue 13 criterion 2: a failed scan has not done what was asked, so the repository is scanned again
        [Test]
        public async Task RunAsync_WhenTheScanAfterTheMessageFailed_ScansAgain()
        {
            CreateRepositoryFolder("orders-api");
            SeedRun(OrdersApiId, ScanRunStatus.Failed, ScanTrigger.Nightly, startedAgo: TimeSpan.FromMinutes(5));
            _queue.Add(Body("orders-api", OrdersApiId), insertedOn: QueuedAgo(TimeSpan.FromMinutes(10)));
            StubScanner scanner = new StubScanner();

            await CreateOrchestrator(scanner).RunAsync(CancellationToken.None);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(scanner.CallCount, Is.EqualTo(1));
                Assert.That(_logger.LatestRecord.GetStructuredStateValue("AlreadyDoneCount"), Is.EqualTo("0"));
            }
        }

        // A copy that arrives while the first copy is still being scanned is not done yet: ADR-027 skips it instead
        [Test]
        public async Task RunAsync_WhenTheFirstCopyIsStillBeingScanned_CountsItAsSkipped()
        {
            CreateRepositoryFolder("orders-api");
            SeedRun(OrdersApiId, ScanRunStatus.Running, ScanTrigger.Nightly, startedAgo: TimeSpan.FromMinutes(2));
            _queue.Add(Body("orders-api", OrdersApiId), insertedOn: QueuedAgo(TimeSpan.FromMinutes(10)));

            await CreateOrchestrator(new StubScanner()).RunAsync(CancellationToken.None);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_logger.LatestRecord.GetStructuredStateValue("SkippedCount"), Is.EqualTo("1"));
                Assert.That(_logger.LatestRecord.GetStructuredStateValue("AlreadyDoneCount"), Is.EqualTo("0"));
            }
        }

        // Strictly after: a scan that started before the message was queued, or at that moment, may not have what it asks
        [TestCase(10)]
        [TestCase(5)]
        public async Task RunAsync_WhenTheGoodScanStartedBeforeTheMessageWasQueued_ScansIt(int startedMinutesAgo)
        {
            CreateRepositoryFolder("orders-api");
            SeedRun(OrdersApiId, ScanRunStatus.Completed, ScanTrigger.Nightly, startedAgo: TimeSpan.FromMinutes(startedMinutesAgo));
            _queue.Add(Body("orders-api", OrdersApiId), insertedOn: QueuedAgo(TimeSpan.FromMinutes(5)));
            StubScanner scanner = new StubScanner();

            await CreateOrchestrator(scanner).RunAsync(CancellationToken.None);

            Assert.That(scanner.CallCount, Is.EqualTo(1));
        }

        // Azure gives InsertedOn to the whole second, so a message shown as queued at 01:55:00 may have been queued at
        // 01:55:00.900. A scan that started at 01:55:00.500 may have started before it, so it does not count.
        [Test]
        public async Task RunAsync_WhenTheGoodScanStartedInTheSameSecondTheMessageWasQueued_ScansIt()
        {
            CreateRepositoryFolder("orders-api");
            SeedRun(OrdersApiId, ScanRunStatus.Completed, ScanTrigger.Nightly, startedAgo: TimeSpan.FromMinutes(5) - TimeSpan.FromMilliseconds(500));
            _queue.Add(Body("orders-api", OrdersApiId), insertedOn: QueuedAgo(TimeSpan.FromMinutes(5)));
            StubScanner scanner = new StubScanner();

            await CreateOrchestrator(scanner).RunAsync(CancellationToken.None);

            Assert.That(scanner.CallCount, Is.EqualTo(1));
        }

        // Issue 13, agreed 2026-10-04: only the same lane counts, so a nightly scan never stands in for "Scan now"
        [Test]
        public async Task RunAsync_WhenTheGoodScanCameFromTheOtherQueue_ScansIt()
        {
            CreateRepositoryFolder("orders-api");
            SeedRun(OrdersApiId, ScanRunStatus.Completed, ScanTrigger.OnDemand, startedAgo: TimeSpan.FromMinutes(5));
            _queue.Add(Body("orders-api", OrdersApiId), insertedOn: QueuedAgo(TimeSpan.FromMinutes(10)));
            StubScanner scanner = new StubScanner();

            await CreateOrchestrator(scanner).RunAsync(CancellationToken.None);

            Assert.That(scanner.CallCount, Is.EqualTo(1));
        }

        // Issue 13 criterion 3: the check comes before the poison check, so a request already done never raises the alert
        [Test]
        public async Task RunAsync_OnTheFourthPickUpOfARequestAlreadyDone_DeletesItInsteadOfPoisoning()
        {
            CreateRepositoryFolder("orders-api");
            SeedRun(OrdersApiId, ScanRunStatus.Completed, ScanTrigger.Nightly, startedAgo: TimeSpan.FromMinutes(5));
            ScanQueueMessage message = _queue.Add(Body("orders-api", OrdersApiId), dequeueCount: 4, insertedOn: QueuedAgo(TimeSpan.FromMinutes(10)));

            await CreateOrchestrator(new StubScanner()).RunAsync(CancellationToken.None);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_queue.Deleted, Is.EqualTo(new[] { message }));
                Assert.That(_queue.Poisoned, Is.Empty);
                Assert.That(_logger.Collector.GetSnapshot().Where(record => record.Level == LogLevel.Warning), Is.Empty);
            }
        }

        // Without the time it was queued, nothing says the message is done, so it is scanned
        [Test]
        public async Task RunAsync_WithNoQueuedTime_ScansIt()
        {
            CreateRepositoryFolder("orders-api");
            SeedRun(OrdersApiId, ScanRunStatus.Completed, ScanTrigger.Nightly, startedAgo: TimeSpan.FromMinutes(5));
            _queue.Add(Body("orders-api", OrdersApiId));
            StubScanner scanner = new StubScanner();

            await CreateOrchestrator(scanner).RunAsync(CancellationToken.None);

            Assert.That(scanner.CallCount, Is.EqualTo(1));
        }

        // 02:50 at +01:00 is 01:50 UTC, before the run that started at 01:55 UTC. Read as 02:50, it would be after it.
        [Test]
        public async Task RunAsync_WithAQueuedTimeInAnotherOffset_ComparesInUtc()
        {
            CreateRepositoryFolder("orders-api");
            SeedRun(OrdersApiId, ScanRunStatus.Completed, ScanTrigger.Nightly, startedAgo: TimeSpan.FromMinutes(5));
            ScanQueueMessage message = _queue.Add(
                Body("orders-api", OrdersApiId), insertedOn: new DateTimeOffset(2026, 10, 1, 2, 50, 0, TimeSpan.FromHours(1)));
            StubScanner scanner = new StubScanner();

            await CreateOrchestrator(scanner).RunAsync(CancellationToken.None);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(scanner.CallCount, Is.Zero);
                Assert.That(_queue.Deleted, Is.EqualTo(new[] { message }));
            }
        }

        // Issue 13: a database error in the already-done check is not caught either. Scanning anyway would hide the
        // outage, and every message would fail the same way, so the run stops and the message is left to reappear.
        [Test]
        public async Task RunAsync_WhenTheAlreadyDoneCheckCannotReachTheDatabase_StopsAndLeavesTheMessage()
        {
            CreateRepositoryFolder("orders-api");
            _queue.Add(Body("orders-api", OrdersApiId), insertedOn: QueuedAgo(TimeSpan.FromMinutes(1)));
            StubScanner scanner = new StubScanner();

            ScanOrchestrator orchestrator = CreateOrchestrator(
                scanner,
                configure: services => services.AddScoped<IScanRunRepository>(
                    provider => new AlreadyDoneCheckFailsScanRunRepository(new ScanRunRepository(provider.GetRequiredService<ScannerDbContext>()))));

            await Assert.ThatAsync(() => orchestrator.RunAsync(CancellationToken.None), Throws.InvalidOperationException);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(scanner.CallCount, Is.Zero);
                Assert.That(_queue.Deleted, Is.Empty);
                Assert.That(_queue.Poisoned, Is.Empty);
            }
        }

        // Issue 8 design, decision 13: every other repository would fail the same way, so the run stops and leaves the
        // messages, rather than failing and in time poisoning each one
        [Test]
        public async Task RunAsync_WhenAzureDevOpsRefusesTheToken_StopsLeavingThatMessageAndTheRest()
        {
            _queue.Add(Body("orders-api", OrdersApiId));
            _queue.Add(Body("billing-api"));
            _source.Failure = new AzureDevOpsAuthenticationException(
                "Azure DevOps refused the token in Scanner:AzureDevOps:Pat for https://dev.azure.com/example. Check that it has not expired or been revoked, and that it has Code (Read).",
                new InvalidOperationException("git clone failed"));

            await Assert.ThatAsync(() => CreateOrchestrator(new StubScanner()).RunAsync(CancellationToken.None), Throws.TypeOf<AzureDevOpsAuthenticationException>());

            using (ScannerDbContext context = _database.CreateContext())
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_queue.Deleted, Is.Empty);
                Assert.That(_queue.Poisoned, Is.Empty);
                Assert.That(_queue.WaitingCount, Is.EqualTo(1), "the second message was taken");
                Assert.That(context.ScanRuns.Single().Status, Is.EqualTo(ScanRunStatus.Failed));
                Assert.That(context.ScanRuns.Single().ErrorMessage, Does.StartWith("Azure DevOps refused the token"));
                Assert.That(_logger.LatestRecord.Message,
                    Is.EqualTo("Stopped taking messages from scan-nightly: Azure DevOps refused the token. 0 scanned, 1 failed, 0 skipped, 0 already done, 0 poisoned"));
            }
        }

        // Issue 10 design, decision 2: the message's lines carry its correlation ID and repository, so one search finds
        // them among the many repositories one execution scans
        [Test]
        public async Task RunAsync_TheLineAboutAReadMessage_CarriesItsCorrelationIdAndRepository()
        {
            CreateRepositoryFolder("orders-api");
            Guid correlationId = new Guid("5b1c6f3e-8a2d-4e7f-9c1a-2d3e4f5a6b7c");
            _queue.Add(Body("orders-api", OrdersApiId, correlationId));

            await CreateOrchestrator(new StubScanner()).RunAsync(CancellationToken.None);

            FakeLogRecord line = _logger.Collector.GetSnapshot().Single(record => record.Message.StartsWith("Message ", StringComparison.Ordinal));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(LogRecordValues.Scope(line, "CorrelationId"), Is.EqualTo(correlationId));
                Assert.That(LogRecordValues.Scope(line, "ProjectName"), Is.EqualTo("Lumina"));
                Assert.That(LogRecordValues.Scope(line, "RepositoryName"), Is.EqualTo("orders-api"));
            }
        }

        // A scope left open by a failed scan would put its ID on every line after it, up to the run's last line
        [Test]
        public async Task RunAsync_AFailedScanThenAnother_EachLineCarriesItsOwnMessage()
        {
            Guid ghostCorrelationId = new Guid("1d2e3f4a-5b6c-4d7e-8f90-a1b2c3d4e5f6");
            Guid ordersCorrelationId = new Guid("6f5e4d3c-2b1a-4c9d-8e7f-0a1b2c3d4e5f");
            _queue.Add(Body("Ghost", correlationId: ghostCorrelationId));
            CreateRepositoryFolder("orders-api");
            _queue.Add(Body("orders-api", correlationId: ordersCorrelationId));

            await CreateOrchestrator(new StubScanner()).RunAsync(CancellationToken.None);

            IReadOnlyList<FakeLogRecord> records = _logger.Collector.GetSnapshot();
            FakeLogRecord failure = records.Single(record => record.Level == LogLevel.Warning);
            FakeLogRecord orders = records.Single(record =>
                record.Message.StartsWith("Message ", StringComparison.Ordinal) && record.GetStructuredStateValue("RepositoryName") == "orders-api");
            FakeLogRecord stopped = records.Single(record => record.Message.StartsWith("Stopped taking messages", StringComparison.Ordinal));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(LogRecordValues.Scope(failure, "CorrelationId"), Is.EqualTo(ghostCorrelationId));
                Assert.That(LogRecordValues.Scope(orders, "CorrelationId"), Is.EqualTo(ordersCorrelationId));
                Assert.That(LogRecordValues.Scope(stopped, "CorrelationId"), Is.Null);
            }
        }

        // Decision 3: a message that cannot be read has no correlation ID to give
        [Test]
        public async Task RunAsync_WithAMalformedMessage_ItsLineHasNoScope()
        {
            _queue.Add("{ \"hello\": 1 }");

            await CreateOrchestrator(new StubScanner()).RunAsync(CancellationToken.None);

            FakeLogRecord warning = _logger.Collector.GetSnapshot().Single(record => record.Level == LogLevel.Warning);
            Assert.That(LogRecordValues.Scope(warning, "CorrelationId"), Is.Null);
        }

        // The lines for a message that is never scanned carry its ID too, so a search on it says what happened
        [TestCase("skipped")]
        [TestCase("poisoned")]
        public async Task RunAsync_TheSkippedAndPoisonedLines_CarryTheirMessagesCorrelationId(string outcome)
        {
            CreateRepositoryFolder("orders-api");
            Guid correlationId = new Guid("2c4e6a8b-0d1f-4b3c-9e5a-7f8b9c0d1e2f");
            if (outcome == "skipped")
            {
                SeedRunningRun(OrdersApiId, startedAgo: TimeSpan.FromMinutes(20));
                _queue.Add(Body("orders-api", OrdersApiId, correlationId));
            }
            else
            {
                _queue.Add(Body("orders-api", OrdersApiId, correlationId), dequeueCount: 4);
            }

            await CreateOrchestrator(new StubScanner()).RunAsync(CancellationToken.None);

            IReadOnlyList<FakeLogRecord> records = _logger.Collector.GetSnapshot();
            FakeLogRecord line = outcome == "skipped"
                ? records.Single(record => record.Message.StartsWith("Skipped", StringComparison.Ordinal))
                : records.Single(record => record.Level == LogLevel.Warning);
            Assert.That(LogRecordValues.Scope(line, "CorrelationId"), Is.EqualTo(correlationId));
        }

        // The services a message's scan uses, as Program registers them, with the in-memory database and the stub
        // scanner in place of the real ones
        private ScanOrchestrator CreateOrchestrator(
            StubScanner scanner, string queueName = "scan-nightly", ExecutionOptions? execution = null, Action<IServiceCollection>? configure = null)
        {
            ServiceCollection services = new ServiceCollection();
            services.AddScoped(_ => _database.CreateContext());
            services.AddScoped<IScanRunRepository, ScanRunRepository>();
            services.AddScoped<IFindingRepository, FindingRepository>();
            services.AddSingleton<IScanner>(scanner);
            services.AddSingleton<TimeProvider>(_time);
            services.AddSingleton<ILogger<RepositoryScanRunner>>(new FakeLogger<RepositoryScanRunner>());
            services.AddScoped<RepositoryScanRunner>();
            configure?.Invoke(services);
            _services = services.BuildServiceProvider();

            return new ScanOrchestrator(
                _logger,
                new HostingEnvironment { EnvironmentName = "Test" },
                Options.Create(new QueueOptions { Name = queueName, ConnectionString = "UseDevelopmentStorage=true" }),
                Options.Create(execution ?? new ExecutionOptions()),
                _queue,
                _source,
                _services.GetRequiredService<IServiceScopeFactory>(),
                _time);
        }

        private void CreateRepositoryFolder(string name)
        {
            Directory.CreateDirectory(Path.Combine(_root.FullPath, name));
        }

        // A Running scan run, as another run would leave it while scanning, or a crash would leave it for good
        private int SeedRunningRun(Guid azureDevOpsId, TimeSpan startedAgo)
        {
            return SeedRun(azureDevOpsId, ScanRunStatus.Running, ScanTrigger.OnDemand, startedAgo);
        }

        // A scan run as an earlier run left it, started startedAgo before the fake clock's time
        private int SeedRun(Guid azureDevOpsId, ScanRunStatus status, ScanTrigger trigger, TimeSpan startedAgo)
        {
            using (ScannerDbContext context = _database.CreateContext())
            {
                Repository repository = context.Repositories.SingleOrDefault(stored => stored.AzureDevOpsId == azureDevOpsId)
                    ?? context.Repositories.Add(new Repository { AzureDevOpsId = azureDevOpsId, ProjectName = "Lumina", Name = "orders-api", DefaultBranch = "develop" }).Entity;
                ScanRun scanRun = new ScanRun
                {
                    Repository = repository,
                    TriggerType = trigger,
                    Status = status,
                    CorrelationId = Guid.NewGuid(),
                    StartedUtc = _time.GetUtcNow().UtcDateTime - startedAgo
                };
                context.ScanRuns.Add(scanRun);
                context.SaveChanges();
                return scanRun.Id;
            }
        }

        // When Azure would have queued a message, that long before the fake clock's time
        private DateTimeOffset QueuedAgo(TimeSpan ago)
        {
            return _time.GetUtcNow() - ago;
        }

        private FakeLogRecord AlreadyDoneLine()
        {
            return _logger.Collector.GetSnapshot().Single(record => record.Message.StartsWith("Already done", StringComparison.Ordinal));
        }

        // A valid message for the repository, in the format of the Issue 7 design
        private static string Body(string repositoryName, Guid? repositoryId = null, Guid? correlationId = null)
        {
            return "{\"schemaVersion\": 1, \"correlationId\": \"" + (correlationId ?? Guid.NewGuid())
                + "\", \"repositoryId\": \"" + (repositoryId ?? Guid.NewGuid())
                + "\", \"projectName\": \"Lumina\", \"repositoryName\": \"" + repositoryName
                + "\", \"defaultBranch\": \"develop\"}";
        }

        // The database out of reach: every call fails, as EF Core does once its retries are spent
        private sealed class UnreachableScanRunRepository : IScanRunRepository
        {
            public Task<ScanRun> StartRunAsync(RepositoryDetails repository, ScanTrigger trigger, Guid correlationId, DateTime startedUtc, CancellationToken cancellationToken)
            {
                throw new InvalidOperationException("The database cannot be reached");
            }

            public Task CompleteRunAsync(int scanRunId, ScanRunCompletion completion, CancellationToken cancellationToken)
            {
                throw new InvalidOperationException("The database cannot be reached");
            }

            public Task<IReadOnlyList<ScanRun>> GetRunningAsync(Guid azureDevOpsId, CancellationToken cancellationToken)
            {
                throw new InvalidOperationException("The database cannot be reached");
            }

            public Task<ScanRun?> GetSuccessfulRunStartedAfterAsync(Guid azureDevOpsId, ScanTrigger trigger, DateTime startedAfterUtc, CancellationToken cancellationToken)
            {
                throw new InvalidOperationException("The database cannot be reached");
            }
        }

        // The real repository, except that the already-done check fails as an unreachable database would. Everything after
        // it works, so a check whose error was caught would go on to scan.
        private sealed class AlreadyDoneCheckFailsScanRunRepository : IScanRunRepository
        {
            private readonly IScanRunRepository _inner;

            public AlreadyDoneCheckFailsScanRunRepository(IScanRunRepository inner)
            {
                _inner = inner;
            }

            public Task<ScanRun> StartRunAsync(RepositoryDetails repository, ScanTrigger trigger, Guid correlationId, DateTime startedUtc, CancellationToken cancellationToken)
            {
                return _inner.StartRunAsync(repository, trigger, correlationId, startedUtc, cancellationToken);
            }

            public Task CompleteRunAsync(int scanRunId, ScanRunCompletion completion, CancellationToken cancellationToken)
            {
                return _inner.CompleteRunAsync(scanRunId, completion, cancellationToken);
            }

            public Task<IReadOnlyList<ScanRun>> GetRunningAsync(Guid azureDevOpsId, CancellationToken cancellationToken)
            {
                return _inner.GetRunningAsync(azureDevOpsId, cancellationToken);
            }

            public Task<ScanRun?> GetSuccessfulRunStartedAfterAsync(Guid azureDevOpsId, ScanTrigger trigger, DateTime startedAfterUtc, CancellationToken cancellationToken)
            {
                throw new InvalidOperationException("The database cannot be reached");
            }
        }

        private static RawFinding Raw(string ruleId, string filePath, int lineNumber)
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
                SecretHash = "hash-a"
            };
        }
    }
}
