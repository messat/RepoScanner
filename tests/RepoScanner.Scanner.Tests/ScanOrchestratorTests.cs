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
using RepoScanner.Scanner.Tests.Queue;

namespace RepoScanner.Scanner.Tests
{
    // The queue loop (#31) with the queue in memory, the repositories in a temporary LocalRoot, a stub scanner and
    // an in-memory database: everything real except the queue, the scanner and the clock
    [TestFixture]
    public class ScanOrchestratorTests
    {
        private SqliteTestDatabase _database = null!;
        private TempFolder _root = null!;
        private FakeScanQueue _queue = null!;
        private FakeTimeProvider _time = null!;
        private FakeLogger<ScanOrchestrator> _logger = null!;
        private ServiceProvider? _services;

        [SetUp]
        public void SetUp()
        {
            _database = new SqliteTestDatabase();
            _root = new TempFolder();
            _queue = new FakeScanQueue();
            _time = new FakeTimeProvider();
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

        // The services a message's scan uses, as Program registers them, with the in-memory database and the stub
        // scanner in place of the real ones
        private ScanOrchestrator CreateOrchestrator(StubScanner scanner, string queueName = "scan-nightly", ExecutionOptions? execution = null)
        {
            ServiceCollection services = new ServiceCollection();
            services.AddScoped(_ => _database.CreateContext());
            services.AddScoped<IScanRunRepository, ScanRunRepository>();
            services.AddScoped<IFindingRepository, FindingRepository>();
            services.AddSingleton<IScanner>(scanner);
            services.AddSingleton<TimeProvider>(_time);
            services.AddSingleton<ILogger<RepositoryScanRunner>>(new FakeLogger<RepositoryScanRunner>());
            services.AddScoped<RepositoryScanRunner>();
            _services = services.BuildServiceProvider();

            return new ScanOrchestrator(
                _logger,
                new HostingEnvironment { EnvironmentName = "Test" },
                Options.Create(new QueueOptions { Name = queueName, ConnectionString = "UseDevelopmentStorage=true" }),
                Options.Create(execution ?? new ExecutionOptions()),
                _queue,
                new LocalFolderSource(Options.Create(new SourceOptions { LocalRoot = _root.FullPath })),
                _services.GetRequiredService<IServiceScopeFactory>(),
                _time);
        }

        private void CreateRepositoryFolder(string name)
        {
            Directory.CreateDirectory(Path.Combine(_root.FullPath, name));
        }

        // A valid message for the repository, in the format of the Issue 7 design
        private static string Body(string repositoryName, Guid? repositoryId = null, Guid? correlationId = null)
        {
            return "{\"schemaVersion\": 1, \"correlationId\": \"" + (correlationId ?? Guid.NewGuid())
                + "\", \"repositoryId\": \"" + (repositoryId ?? Guid.NewGuid())
                + "\", \"projectName\": \"Lumina\", \"repositoryName\": \"" + repositoryName
                + "\", \"defaultBranch\": \"develop\"}";
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
