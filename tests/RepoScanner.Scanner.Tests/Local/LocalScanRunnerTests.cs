using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Testing;
using NUnit.Framework;
using RepoScanner.Core.Entities;
using RepoScanner.Core.Enums;
using RepoScanner.Data;
using RepoScanner.Data.Repositories;
using RepoScanner.Scanner.Local;

namespace RepoScanner.Scanner.Tests.Local
{
    // What --path adds to RepositoryScanRunner: the folder described as a Local repository, on demand. The
    // scan itself is tested in RepositoryScanRunnerTests.
    [TestFixture]
    public class LocalScanRunnerTests
    {
        // The runner never reads this folder itself: the stub scanner stands in for the real one
        private static readonly string Folder = Path.Combine(Path.GetTempPath(), "orders-api");

        private SqliteTestDatabase _database = null!;

        [SetUp]
        public void SetUp()
        {
            _database = new SqliteTestDatabase();
        }

        [TearDown]
        public void TearDown()
        {
            _database.Dispose();
        }

        [Test]
        public async Task RunAsync_AddsTheFolderAsALocalRepository()
        {
            StubScanner scanner = new StubScanner();

            await RunAsync(scanner);

            using (ScannerDbContext context = _database.CreateContext())
            {
                Repository repository = context.Repositories.Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(repository.ProjectName, Is.EqualTo("Local"));
                    Assert.That(repository.Name, Is.EqualTo("orders-api"));
                    Assert.That(repository.DefaultBranch, Is.EqualTo("local"));
                    Assert.That(repository.AzureDevOpsId, Is.EqualTo(LocalScanRunner.DescribeFolder(Folder).AzureDevOpsId));
                    Assert.That(context.ScanRuns.Single().TriggerType, Is.EqualTo(ScanTrigger.OnDemand));
                    Assert.That(scanner.RootPath, Is.EqualTo(Folder));
                }
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

        private async Task RunAsync(StubScanner scanner)
        {
            using (ScannerDbContext context = _database.CreateContext())
            {
                RepositoryScanRunner runner = new RepositoryScanRunner(
                    new ScanRunRepository(context), new FindingRepository(context), new[] { scanner }, TimeProvider.System, new FakeLogger<RepositoryScanRunner>());

                await new LocalScanRunner(runner).RunAsync(Folder, CancellationToken.None);
            }
        }
    }
}
