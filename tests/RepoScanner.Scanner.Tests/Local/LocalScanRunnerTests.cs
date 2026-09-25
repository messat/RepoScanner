using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
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

        // Design decision 2 (interim): a second run of the same folder adds nothing, rather than failing
        // on the unique fingerprint
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

        private async Task RunAsync(IScanner scanner)
        {
            using (ScannerDbContext context = _database.CreateContext())
            {
                LocalScanRunner runner = new LocalScanRunner(new ScanRunRepository(context), new FindingRepository(context), new IScanner[] { scanner }, _logger);

                await runner.RunAsync(Folder, CancellationToken.None);
            }
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
