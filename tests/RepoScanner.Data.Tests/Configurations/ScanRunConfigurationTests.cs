using System;
using System.Linq;
using NUnit.Framework;
using RepoScanner.Core.Entities;
using RepoScanner.Core.Enums;

namespace RepoScanner.Data.Tests.Configurations
{
    [TestFixture]
    public class ScanRunConfigurationTests
    {
        private TestDatabase _database;

        [SetUp]
        public void SetUp()
        {
            _database = new TestDatabase();
        }

        [TearDown]
        public void TearDown()
        {
            _database.Dispose();
        }

        [Test]
        public void SaveAndReload_KeepsEveryColumn()
        {
            Guid correlationId = new Guid("0b7d2f4e-8c1a-4d3b-9e6f-2a5c7e9b1d3f");
            DateTime startedUtc = new DateTime(2026, 9, 24, 2, 0, 0, DateTimeKind.Utc);
            DateTime completedUtc = new DateTime(2026, 9, 24, 2, 6, 30, DateTimeKind.Utc);
            using (ScannerDbContext context = _database.CreateContext())
            {
                Repository repository = TestEntities.NewRepository();
                repository.ScanRuns.Add(new ScanRun
                {
                    TriggerType = ScanTrigger.OnDemand,
                    Status = ScanRunStatus.PartiallyCompleted,
                    CommitSha = "9fceb02d0ae598e95dc970b74767f19372d61af8",
                    CorrelationId = correlationId,
                    StartedUtc = startedUtc,
                    CompletedUtc = completedUtc,
                    NewCount = 3,
                    ReopenedCount = 1,
                    ResolvedCount = 2,
                    ErrorMessage = "Package scanner failed"
                });
                context.Repositories.Add(repository);
                context.SaveChanges();
            }

            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun saved = context.ScanRuns.Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(saved.Id, Is.GreaterThan(0));
                    Assert.That(saved.RepositoryId, Is.EqualTo(context.Repositories.Single().Id));
                    Assert.That(saved.TriggerType, Is.EqualTo(ScanTrigger.OnDemand));
                    Assert.That(saved.Status, Is.EqualTo(ScanRunStatus.PartiallyCompleted));
                    Assert.That(saved.CommitSha, Is.EqualTo("9fceb02d0ae598e95dc970b74767f19372d61af8"));
                    Assert.That(saved.CorrelationId, Is.EqualTo(correlationId));
                    Assert.That(saved.StartedUtc, Is.EqualTo(startedUtc));
                    Assert.That(saved.CompletedUtc, Is.EqualTo(completedUtc));
                    Assert.That(saved.NewCount, Is.EqualTo(3));
                    Assert.That(saved.ReopenedCount, Is.EqualTo(1));
                    Assert.That(saved.ResolvedCount, Is.EqualTo(2));
                    Assert.That(saved.ErrorMessage, Is.EqualTo("Package scanner failed"));
                }
            }
        }

        // A run is saved when the scan starts, before the commit, completion time or any error is known
        [Test]
        public void Save_OfARunningRunWithOnlyRequiredColumns_IsAccepted()
        {
            using (ScannerDbContext context = _database.CreateContext())
            {
                Repository repository = TestEntities.NewRepository();
                repository.ScanRuns.Add(TestEntities.NewScanRun());
                context.Repositories.Add(repository);

                Assert.That(() => context.SaveChanges(), Throws.Nothing);
            }
        }

        // Enums are stored as their names, so the SQL reads 'OnDemand', not 1
        [Test]
        public void Save_StoresTheEnumsAsText()
        {
            using (ScannerDbContext context = _database.CreateContext())
            {
                Repository repository = TestEntities.NewRepository();
                ScanRun scanRun = TestEntities.NewScanRun();
                scanRun.TriggerType = ScanTrigger.OnDemand;
                scanRun.Status = ScanRunStatus.Completed;
                repository.ScanRuns.Add(scanRun);
                context.Repositories.Add(repository);
                context.SaveChanges();
            }

            object? stored = _database.ExecuteScalar("SELECT TriggerType || '/' || Status FROM ScanRuns");

            Assert.That(stored, Is.EqualTo("OnDemand/Completed"));
        }
    }
}
