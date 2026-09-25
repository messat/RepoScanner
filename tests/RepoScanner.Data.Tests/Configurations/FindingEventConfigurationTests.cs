using System;
using System.Linq;
using NUnit.Framework;
using RepoScanner.Core.Entities;
using RepoScanner.Core.Enums;

namespace RepoScanner.Data.Tests.Configurations
{
    [TestFixture]
    public class FindingEventConfigurationTests
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

        // An event raised during a scan, so every column is set, including ScanRunId
        [Test]
        public void SaveAndReload_KeepsEveryColumn()
        {
            DateTime occurredUtc = new DateTime(2026, 9, 24, 2, 4, 0, DateTimeKind.Utc);
            using (ScannerDbContext context = _database.CreateContext())
            {
                Repository repository = TestEntities.NewRepository();
                ScanRun scanRun = TestEntities.NewScanRun();
                Finding finding = TestEntities.NewFinding("a1");
                repository.ScanRuns.Add(scanRun);
                repository.Findings.Add(finding);
                finding.Events.Add(new FindingEvent
                {
                    ScanRun = scanRun,
                    EventType = FindingEventType.Reopened,
                    OccurredUtc = occurredUtc,
                    Actor = "scanner",
                    Detail = "Found again after being resolved"
                });
                context.Repositories.Add(repository);
                context.SaveChanges();
            }

            using (ScannerDbContext context = _database.CreateContext())
            {
                FindingEvent saved = context.FindingEvents.Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(saved.Id, Is.GreaterThan(0));
                    Assert.That(saved.FindingId, Is.EqualTo(context.Findings.Single().Id));
                    Assert.That(saved.ScanRunId, Is.EqualTo(context.ScanRuns.Single().Id));
                    Assert.That(saved.EventType, Is.EqualTo(FindingEventType.Reopened));
                    Assert.That(saved.OccurredUtc, Is.EqualTo(occurredUtc));
                    Assert.That(saved.Actor, Is.EqualTo("scanner"));
                    Assert.That(saved.Detail, Is.EqualTo("Found again after being resolved"));
                }
            }
        }

        // A developer's action in the dashboard is not part of any scan, so it has no ScanRunId
        [Test]
        public void Save_OfADashboardActionWithoutAScanRun_IsAccepted()
        {
            using (ScannerDbContext context = _database.CreateContext())
            {
                Repository repository = TestEntities.NewRepository();
                Finding finding = TestEntities.NewFinding("a1");
                repository.Findings.Add(finding);
                finding.Events.Add(new FindingEvent
                {
                    EventType = FindingEventType.Suppressed,
                    OccurredUtc = TestEntities.StartedUtc,
                    Actor = "developer@example.com"
                });
                context.Repositories.Add(repository);

                Assert.That(() => context.SaveChanges(), Throws.Nothing);
            }
        }

        // Enums are stored as their names, so the SQL reads 'SuppressionExpired', not 4
        [Test]
        public void Save_StoresTheEnumAsText()
        {
            using (ScannerDbContext context = _database.CreateContext())
            {
                Repository repository = TestEntities.NewRepository();
                Finding finding = TestEntities.NewFinding("a1");
                repository.Findings.Add(finding);
                finding.Events.Add(new FindingEvent { EventType = FindingEventType.SuppressionExpired, Actor = "scanner" });
                context.Repositories.Add(repository);
                context.SaveChanges();
            }

            object? stored = _database.ExecuteScalar("SELECT EventType FROM FindingEvents");

            Assert.That(stored, Is.EqualTo("SuppressionExpired"));
        }
    }
}
