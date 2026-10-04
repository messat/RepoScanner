using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RepoScanner.Core.Entities;
using RepoScanner.Core.Enums;
using RepoScanner.Data;
using RepoScanner.Scanner.Queue;
using RepoScanner.Scanner.Sources;
using RepoScanner.Scanner.Tests.Local;
using RepoScanner.Scanner.Tests.Packages;
using RepoScanner.Scanner.Tests.Queue;
using RepoScanner.Scanner.Tests.Secrets;
using RepoScanner.Scanner.Tests.Sources;

namespace RepoScanner.Scanner.Tests
{
    // Issue 7's success criteria (#35), one test each, through the whole program in queue mode as dotnet run starts
    // it: real settings, scanners, loop, clone and database code, with an in-memory queue, in-memory SQLite and a
    // stand-in for nuget.org. The repository is a bare copy of the secrets fixture, cloned with the real git as any
    // queued repository is (Issue 8). A missing queue exiting 2 is in ProgramTests.
    [TestFixture]
    public class QueueModeTests
    {
        // A fake PAT: values starting "your-" are placeholders to RepoScanner's own self-scan
        private const string Pat = "your-test-pat-0001";

        // The secrets fixture's Azure DevOps ID, as a message would give it
        private static readonly Guid SecretsRepoId = new Guid("3c9f1a7e-5b2d-4e8f-a1c6-9d0e2f4b7a85");

        // A repository that does not exist, so its clone fails
        private static readonly Guid GhostId = new Guid("7d2e4f6a-1b3c-4d5e-8f9a-0b1c2d3e4f5a");

        // What git sends for the PAT: Basic credentials with an empty user name
        private static readonly string Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + Pat));

        // <_repos>/Lumina/_git/secrets-repo, made once: the tests only read it
        private TempFolder _repos = null!;

        // The commit a clone of the bare copy gets
        private string _commitId = null!;
        private TempFolder _workspaces = null!;
        private SqliteTestDatabase _database = null!;
        private FakeNuGet _nuGet = null!;
        private FakeScanQueue _queue = null!;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            _repos = new TempFolder();
            _commitId = BareRepository.Create(_repos.FullPath, "Lumina", "secrets-repo", "develop", FixtureSecrets.FolderPath);
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            _repos.Dispose();
        }

        [SetUp]
        public void SetUp()
        {
            _workspaces = new TempFolder();
            _database = new SqliteTestDatabase();
            _nuGet = new FakeNuGet();
            _queue = new FakeScanQueue();
        }

        [TearDown]
        public void TearDown()
        {
            _database.Dispose();
            _nuGet.Dispose();
            _workspaces.Dispose();
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

            JsonElement poisoned = LogLines.Read(output).Single(line => LogLines.Value(line, "PoisonQueueName") != null);
            using (ScannerDbContext context = _database.CreateContext())
            using (Assert.EnterMultipleScope())
            {
                Assert.That(exitCode, Is.EqualTo(0));
                Assert.That(_queue.Poisoned, Is.EqualTo(new[] { malformed }));
                Assert.That(LogLines.Value(poisoned, "PoisonQueueName"), Is.EqualTo(poisonQueueName));
                Assert.That(LogLines.Value(poisoned, "Reason"), Does.StartWith("The message is not JSON"));
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

            JsonElement skipped = LogLines.Read(output).Single(line => LogLines.Message(line).StartsWith("Skipped", StringComparison.Ordinal));
            using (ScannerDbContext context = _database.CreateContext())
            using (Assert.EnterMultipleScope())
            {
                Assert.That(exitCode, Is.EqualTo(0));
                Assert.That(_queue.Deleted, Is.EqualTo(new[] { message }));
                Assert.That(LogLines.Value(skipped, "RepositoryName"), Is.EqualTo("secrets-repo"));
                Assert.That(LogLines.Value(skipped, "ScanRunId"), Is.EqualTo(running.ToString(CultureInfo.InvariantCulture)));
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

            JsonElement takenOver = LogLines.Read(output).Single(line => LogLines.Message(line).Contains("was abandoned", StringComparison.Ordinal));
            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun old = context.ScanRuns.Single(run => run.Id == abandoned);
                ScanRun scanned = context.ScanRuns.Single(run => run.Id != abandoned);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(exitCode, Is.EqualTo(0));
                    Assert.That(old.Status, Is.EqualTo(ScanRunStatus.Failed));
                    Assert.That(old.ErrorMessage, Does.StartWith("Abandoned: still Running more than 20 minutes"));
                    Assert.That(LogLines.Value(takenOver, "ScanRunId"), Is.EqualTo(abandoned.ToString(CultureInfo.InvariantCulture)));
                    Assert.That(LogLines.Value(takenOver, "RepositoryName"), Is.EqualTo("secrets-repo"));
                    Assert.That(scanned.Status, Is.EqualTo(ScanRunStatus.Completed));
                    Assert.That(context.Findings.Count(), Is.EqualTo(FixtureSecrets.All.Count));
                    Assert.That(_queue.Deleted, Is.EqualTo(new[] { message }));
                }
            }
        }

        // Issue 8 criterion 1: a queue message for a repository produces findings from the cloned source, and the scan
        // run records which commit they came from
        [Test]
        public async Task Issue8_AMessage_ClonesTheRepositoryAndSavesItsFindingsAndCommitId()
        {
            _queue.Add(Body("secrets-repo"));

            (int exitCode, _) = await RunAsync("scan-nightly");

            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun run = context.ScanRuns.Single();
                Finding[] findings = context.Findings.AsEnumerable()
                    .OrderBy(finding => finding.FilePath, StringComparer.Ordinal)
                    .ThenBy(finding => finding.LineNumber)
                    .ToArray();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(exitCode, Is.EqualTo(0));
                    Assert.That(run.Status, Is.EqualTo(ScanRunStatus.Completed));
                    Assert.That(run.CommitSha, Is.EqualTo(_commitId));
                    Assert.That(findings.Select(finding => (finding.RuleId, finding.FilePath, finding.LineNumber)),
                        Is.EqualTo(FixtureSecrets.All.Select(secret => (secret.RuleId, secret.FilePath, (int?)secret.LineNumber))));
                }
            }
        }

        // Issue 8 criterion 2, with a failed clone as well, since an error message is where a token would most likely
        // leak. Everything the program printed is checked, as a Container Apps Job's log would hold it.
        [Test]
        public async Task Issue8_NothingTheProgramPrints_ContainsThePat()
        {
            _queue.Add(Body("secrets-repo"));
            _queue.Add(Body("ghost", GhostId));

            (int exitCode, string output) = await RunAsync("scan-nightly");

            // Searched as printed and, line by line, unescaped, so JSON's escaping cannot hide a leaked PAT (Review Focus 2)
            bool leaked = output.Contains(Pat, StringComparison.Ordinal) || output.Contains(Base64, StringComparison.Ordinal)
                || LogLines.Read(output).SelectMany(LogLines.Strings).Any(text => text.Contains(Pat, StringComparison.Ordinal) || text.Contains(Base64, StringComparison.Ordinal));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(exitCode, Is.EqualTo(0));
                Assert.That(output, Does.Contain("git clone failed with exit code 128"), "the failed clone was not reported, so the check proves little");
                Assert.That(leaked, Is.False, "the PAT is in what the program printed");
            }
        }

        // Issue 8 criterion 3: no clone is left behind, whether its scan completed or its clone failed
        [Test]
        public async Task Issue8_TheWorkspaceRoot_IsEmptyAfterACompletedAndAFailedScan()
        {
            _queue.Add(Body("secrets-repo"));
            _queue.Add(Body("ghost", GhostId));

            await RunAsync("scan-nightly");

            using (ScannerDbContext context = _database.CreateContext())
            using (Assert.EnterMultipleScope())
            {
                Assert.That(context.ScanRuns.OrderBy(run => run.Id).Select(run => run.Status),
                    Is.EqualTo(new[] { ScanRunStatus.Completed, ScanRunStatus.Failed }));
                Assert.That(Directory.EnumerateFileSystemEntries(_workspaces.FullPath), Is.Empty);
            }
        }

        // The program as dotnet run starts it in queue mode. The organisation is the folder of bare repositories, so a
        // message for Lumina/secrets-repo clones the bare copy of tests/fixtures/secrets-repo. The clones go in a test
        // folder, never the real %TEMP%\reposcanner.
        private async Task<(int ExitCode, string Output)> RunAsync(string queueName)
        {
            string[] args =
            {
                "--Scanner:Queue:Name", queueName,
                "--Scanner:Queue:ConnectionString", "UseDevelopmentStorage=true",
                "--Scanner:Database:ConnectionString", "Server=unused;Database=unused",
                "--Scanner:AzureDevOps:OrganizationUrl", BareRepository.OrganizationUrl(_repos.FullPath),
                "--Scanner:AzureDevOps:Pat", Pat
            };

            int exitCode = 0;
            string output = await ConsoleCapture.RunAsync(async () =>
            {
                exitCode = await Program.RunAsync(args, services =>
                {
                    _database.ReplaceDatabaseIn(services);
                    _nuGet.ReplaceNuGetOrg(services);
                    services.AddSingleton<IScanQueue>(_queue);
                    services.AddSingleton(new WorkspaceFolders(_workspaces.FullPath, NullLogger<WorkspaceFolders>.Instance));
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

        private static string Body(string repositoryName, Guid? repositoryId = null)
        {
            return "{\"schemaVersion\": 1, \"correlationId\": \"" + Guid.NewGuid() + "\", \"repositoryId\": \"" + (repositoryId ?? SecretsRepoId)
                + "\", \"projectName\": \"Lumina\", \"repositoryName\": \"" + repositoryName + "\", \"defaultBranch\": \"develop\"}";
        }
    }
}
