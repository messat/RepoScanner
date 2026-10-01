using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using RepoScanner.Core.Entities;
using RepoScanner.Core.Enums;
using RepoScanner.Data;
using RepoScanner.Scanner.Queue;
using RepoScanner.Scanner.Tests.Local;
using RepoScanner.Scanner.Tests.Packages;
using RepoScanner.Scanner.Tests.Queue;
using RepoScanner.Scanner.Tests.Secrets;

namespace RepoScanner.Scanner.Tests
{
    // Issue 7's success criteria (#35), one test each, through the whole program in queue mode as dotnet run starts
    // it: real settings, scanners, loop and database code, with an in-memory queue, in-memory SQLite and a stand-in
    // for nuget.org. The repository is the secrets fixture, found in LocalRoot like any queued repository. A missing
    // queue exiting 2 is in ProgramTests.
    [TestFixture]
    public class QueueModeTests
    {
        // The secrets fixture's Azure DevOps ID, as a message would give it
        private static readonly Guid SecretsRepoId = new Guid("3c9f1a7e-5b2d-4e8f-a1c6-9d0e2f4b7a85");

        private SqliteTestDatabase _database = null!;
        private FakeNuGet _nuGet = null!;
        private FakeScanQueue _queue = null!;

        [SetUp]
        public void SetUp()
        {
            _database = new SqliteTestDatabase();
            _nuGet = new FakeNuGet();
            _queue = new FakeScanQueue();
        }

        [TearDown]
        public void TearDown()
        {
            _database.Dispose();
            _nuGet.Dispose();
            Assert.That(_nuGet.UnknownAddresses, Is.Empty, "requests that would have reached the internet");
        }

        // Criterion 1: one local message produces one ScanRun and findings
        [Test]
        public async Task OneMessage_ProducesOneScanRunWithTheFixturesFindings()
        {
            ScanQueueMessage message = _queue.Add(Body("secrets-repo"));

            (int exitCode, _) = await RunAsync("scan-nightly");

            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun run = context.ScanRuns.Include(saved => saved.Repository).Single();
                Finding[] findings = context.Findings.AsEnumerable()
                    .OrderBy(finding => finding.FilePath, StringComparer.Ordinal)
                    .ThenBy(finding => finding.LineNumber)
                    .ToArray();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(exitCode, Is.EqualTo(0));
                    Assert.That(run.Status, Is.EqualTo(ScanRunStatus.Completed));
                    Assert.That(run.TriggerType, Is.EqualTo(ScanTrigger.Nightly));
                    Assert.That(run.Repository.AzureDevOpsId, Is.EqualTo(SecretsRepoId));
                    Assert.That(run.Repository.Name, Is.EqualTo("secrets-repo"));
                    Assert.That(findings.Select(finding => (finding.RuleId, finding.FilePath, finding.LineNumber)),
                        Is.EqualTo(FixtureSecrets.All.Select(secret => (secret.RuleId, secret.FilePath, (int?)secret.LineNumber))));
                    Assert.That(run.NewCount, Is.EqualTo(FixtureSecrets.All.Count));
                    Assert.That(_queue.Deleted, Is.EqualTo(new[] { message }));
                }
            }
        }

        // Criterion 2: an empty queue exits 0 within seconds
        [Test]
        public async Task EmptyQueue_ExitsZeroWithinSeconds()
        {
            Stopwatch elapsed = Stopwatch.StartNew();

            (int exitCode, string output) = await RunAsync("scan-nightly");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(exitCode, Is.EqualTo(0));
                Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)));
                Assert.That(output, Does.Contain("queue empty"));
            }
        }

        // Criterion 3: a malformed message lands in its lane's poison queue, with no scan run
        [TestCase("scan-nightly", "scan-nightly-poison")]
        [TestCase("scan-ondemand", "scan-ondemand-poison")]
        public async Task MalformedMessage_LandsInItsLanesPoisonQueue(string queueName, string poisonQueueName)
        {
            ScanQueueMessage malformed = _queue.Add("{\"hello: 1}");

            (int exitCode, string output) = await RunAsync(queueName);

            using (ScannerDbContext context = _database.CreateContext())
            using (Assert.EnterMultipleScope())
            {
                Assert.That(exitCode, Is.EqualTo(0));
                Assert.That(_queue.Poisoned, Is.EqualTo(new[] { malformed }));
                Assert.That(output, Does.Contain("moved to " + poisonQueueName + ": The message is not JSON"));
                Assert.That(context.ScanRuns.Count(), Is.Zero);
            }
        }

        // Criterion 4: a message for a repository with a live Running ScanRun is deleted and logged as skipped
        [Test]
        public async Task MessageForARepositoryBeingScanned_IsDeletedAndLoggedAsSkipped()
        {
            int running = SeedRunningRun(DateTime.UtcNow.AddMinutes(-2));
            ScanQueueMessage message = _queue.Add(Body("secrets-repo"));

            (int exitCode, string output) = await RunAsync("scan-nightly");

            using (ScannerDbContext context = _database.CreateContext())
            using (Assert.EnterMultipleScope())
            {
                Assert.That(exitCode, Is.EqualTo(0));
                Assert.That(_queue.Deleted, Is.EqualTo(new[] { message }));
                Assert.That(output, Does.Contain("Skipped Lumina/secrets-repo").And.Contain("scan run " + running + " is already scanning it"));
                Assert.That(context.ScanRuns.Count(), Is.EqualTo(1), "no new scan run");
                Assert.That(context.Findings.Count(), Is.Zero);
            }
        }

        // Criterion 5: a Running ScanRun older than ScanTimeoutPerRepoMinutes + 5 minutes is marked Failed and the
        // repository is scanned
        [Test]
        public async Task RunningScanRunOlderThanTheLimitPlusFive_IsMarkedFailedAndTheRepositoryScanned()
        {
            int abandoned = SeedRunningRun(DateTime.UtcNow.AddMinutes(-30));
            ScanQueueMessage message = _queue.Add(Body("secrets-repo"));

            (int exitCode, string output) = await RunAsync("scan-nightly");

            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun old = context.ScanRuns.Single(run => run.Id == abandoned);
                ScanRun scanned = context.ScanRuns.Single(run => run.Id != abandoned);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(exitCode, Is.EqualTo(0));
                    Assert.That(old.Status, Is.EqualTo(ScanRunStatus.Failed));
                    Assert.That(old.ErrorMessage, Does.StartWith("Abandoned: still Running more than 20 minutes"));
                    Assert.That(output, Does.Contain("Scan run " + abandoned + " for Lumina/secrets-repo was abandoned"));
                    Assert.That(scanned.Status, Is.EqualTo(ScanRunStatus.Completed));
                    Assert.That(context.Findings.Count(), Is.EqualTo(FixtureSecrets.All.Count));
                    Assert.That(_queue.Deleted, Is.EqualTo(new[] { message }));
                }
            }
        }

        // The program as dotnet run starts it in queue mode. LocalRoot is the fixtures folder, so a message for
        // secrets-repo scans tests/fixtures/secrets-repo.
        private async Task<(int ExitCode, string Output)> RunAsync(string queueName)
        {
            string[] args =
            {
                "--Scanner:Queue:Name", queueName,
                "--Scanner:Queue:ConnectionString", "UseDevelopmentStorage=true",
                "--Scanner:Database:ConnectionString", "Server=unused;Database=unused",
                "--Scanner:Source:LocalRoot", Path.GetDirectoryName(FixtureSecrets.FolderPath)!
            };

            int exitCode = 0;
            string output = await ConsoleCapture.RunAsync(async () =>
            {
                exitCode = await Program.RunAsync(args, services =>
                {
                    _database.ReplaceDatabaseIn(services);
                    _nuGet.ReplaceNuGetOrg(services);
                    services.AddSingleton<IScanQueue>(_queue);
                });
            });

            return (exitCode, output);
        }

        // A Running scan run for the secrets fixture, as another run would leave it, or a crash would
        private int SeedRunningRun(DateTime startedUtc)
        {
            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun scanRun = new ScanRun
                {
                    Repository = new Repository { AzureDevOpsId = SecretsRepoId, ProjectName = "Lumina", Name = "secrets-repo", DefaultBranch = "develop" },
                    TriggerType = ScanTrigger.OnDemand,
                    Status = ScanRunStatus.Running,
                    CorrelationId = Guid.NewGuid(),
                    StartedUtc = startedUtc
                };
                context.ScanRuns.Add(scanRun);
                context.SaveChanges();
                return scanRun.Id;
            }
        }

        private static string Body(string repositoryName)
        {
            return "{\"schemaVersion\": 1, \"correlationId\": \"" + Guid.NewGuid() + "\", \"repositoryId\": \"" + SecretsRepoId
                + "\", \"projectName\": \"Lumina\", \"repositoryName\": \"" + repositoryName + "\", \"defaultBranch\": \"develop\"}";
        }
    }
}
