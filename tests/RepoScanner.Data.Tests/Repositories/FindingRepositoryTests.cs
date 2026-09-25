using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RepoScanner.Core.Entities;
using RepoScanner.Core.Enums;
using RepoScanner.Core.Persistence;
using RepoScanner.Data.Repositories;

namespace RepoScanner.Data.Tests.Repositories
{
    [TestFixture]
    public class FindingRepositoryTests
    {
        private static readonly DateTime LaterUtc = new DateTime(2026, 9, 25, 2, 5, 0, DateTimeKind.Utc);

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

        // The reconciler needs resolved findings to reopen them, and suppressed ones to leave them alone
        [Test]
        public async Task GetByRepositoryAsync_ReturnsFindingsOfEveryStatus()
        {
            Finding resolved = TestEntities.NewFinding("a2");
            resolved.Status = FindingStatus.Resolved;
            Finding suppressed = TestEntities.NewFinding("a3");
            suppressed.Status = FindingStatus.Suppressed;
            suppressed.SuppressionReason = "False positive";
            int repositoryId = SeedRepository(TestEntities.NewFinding("a1"), resolved, suppressed);

            IReadOnlyList<Finding> findings;
            using (ScannerDbContext context = _database.CreateContext())
            {
                FindingRepository repository = new FindingRepository(context);

                findings = await repository.GetByRepositoryAsync(repositoryId, CancellationToken.None);
            }

            Assert.That(findings.Select(finding => finding.Fingerprint), Is.EquivalentTo(new string[] { "a1", "a2", "a3" }));
        }

        [Test]
        public async Task GetByRepositoryAsync_ReturnsOnlyThatRepositorysFindings()
        {
            int repositoryId = SeedRepository(TestEntities.NewFinding("a1"));
            SeedRepository(TestEntities.NewFinding("b1"));

            IReadOnlyList<Finding> findings;
            using (ScannerDbContext context = _database.CreateContext())
            {
                FindingRepository repository = new FindingRepository(context);

                findings = await repository.GetByRepositoryAsync(repositoryId, CancellationToken.None);
            }

            Assert.That(findings.Select(finding => finding.Fingerprint), Is.EquivalentTo(new string[] { "a1" }));
        }

        // A new finding is saved together with its Detected event
        [Test]
        public async Task SaveChangesAsync_AddsNewFindingsWithTheirEvents()
        {
            int repositoryId = SeedRepository();
            Finding finding = TestEntities.NewFinding("a1");
            finding.RepositoryId = repositoryId;
            finding.Events.Add(new FindingEvent { EventType = FindingEventType.Detected, OccurredUtc = LaterUtc, Actor = "scanner" });

            using (ScannerDbContext context = _database.CreateContext())
            {
                FindingRepository repository = new FindingRepository(context);

                await repository.SaveChangesAsync(Changes(newFindings: new Finding[] { finding }), CancellationToken.None);
            }

            using (ScannerDbContext context = _database.CreateContext())
            {
                Finding saved = context.Findings.Single();
                FindingEvent savedEvent = context.FindingEvents.Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(saved.Fingerprint, Is.EqualTo("a1"));
                    Assert.That(saved.RepositoryId, Is.EqualTo(repositoryId));
                    Assert.That(savedEvent.FindingId, Is.EqualTo(saved.Id));
                    Assert.That(savedEvent.EventType, Is.EqualTo(FindingEventType.Detected));
                }
            }
        }

        // The usual path: the reconciler changes findings it loaded through the same repository
        [Test]
        public async Task SaveChangesAsync_SavesChangesToFindingsItLoaded()
        {
            int repositoryId = SeedRepository(TestEntities.NewFinding("a1"));

            using (ScannerDbContext context = _database.CreateContext())
            {
                FindingRepository repository = new FindingRepository(context);
                Finding finding = (await repository.GetByRepositoryAsync(repositoryId, CancellationToken.None)).Single();
                finding.Status = FindingStatus.Resolved;
                finding.ResolvedUtc = LaterUtc;

                await repository.SaveChangesAsync(Changes(updatedFindings: new Finding[] { finding }), CancellationToken.None);
            }

            using (ScannerDbContext context = _database.CreateContext())
            {
                Finding saved = context.Findings.Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(saved.Status, Is.EqualTo(FindingStatus.Resolved));
                    Assert.That(saved.ResolvedUtc, Is.EqualTo(LaterUtc));
                }
            }
        }

        // A finding that was not loaded by this repository must still be saved, not silently ignored
        [Test]
        public async Task SaveChangesAsync_SavesChangesToFindingsLoadedElsewhere()
        {
            SeedRepository(TestEntities.NewFinding("a1"));
            Finding finding;
            using (ScannerDbContext context = _database.CreateContext())
            {
                finding = context.Findings.Single();
            }

            finding.LastSeenUtc = LaterUtc;
            using (ScannerDbContext context = _database.CreateContext())
            {
                FindingRepository repository = new FindingRepository(context);

                await repository.SaveChangesAsync(Changes(updatedFindings: new Finding[] { finding }), CancellationToken.None);
            }

            using (ScannerDbContext context = _database.CreateContext())
            {
                Assert.That(context.Findings.Single().LastSeenUtc, Is.EqualTo(LaterUtc));
            }
        }

        // The dashboard may suppress a finding while a scan is running. The scanner only changed
        // LastSeenUtc, so saving must not write back the Status and suppression it loaded earlier.
        [Test]
        public async Task SaveChangesAsync_OnlyWritesTheColumnsTheScannerChanged()
        {
            int repositoryId = SeedRepository(TestEntities.NewFinding("a1"));

            using (ScannerDbContext scannerContext = _database.CreateContext())
            {
                FindingRepository repository = new FindingRepository(scannerContext);
                Finding finding = (await repository.GetByRepositoryAsync(repositoryId, CancellationToken.None)).Single();

                using (ScannerDbContext dashboardContext = _database.CreateContext())
                {
                    Finding sameFinding = dashboardContext.Findings.Single();
                    sameFinding.Status = FindingStatus.Suppressed;
                    sameFinding.SuppressionReason = "Test credentials";
                    sameFinding.SuppressedBy = "developer@example.com";
                    dashboardContext.SaveChanges();
                }

                finding.LastSeenUtc = LaterUtc;
                await repository.SaveChangesAsync(Changes(updatedFindings: new Finding[] { finding }), CancellationToken.None);
            }

            using (ScannerDbContext context = _database.CreateContext())
            {
                Finding saved = context.Findings.Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(saved.LastSeenUtc, Is.EqualTo(LaterUtc));
                    Assert.That(saved.Status, Is.EqualTo(FindingStatus.Suppressed));
                    Assert.That(saved.SuppressionReason, Is.EqualTo("Test credentials"));
                    Assert.That(saved.SuppressedBy, Is.EqualTo("developer@example.com"));
                }
            }
        }

        // For example the Resolved event of a finding the scan no longer found
        [Test]
        public async Task SaveChangesAsync_AddsNewEventsForExistingFindings()
        {
            SeedRepository(TestEntities.NewFinding("a1"));
            int findingId;
            using (ScannerDbContext context = _database.CreateContext())
            {
                findingId = context.Findings.Single().Id;
            }

            FindingEvent resolvedEvent = new FindingEvent
            {
                FindingId = findingId,
                EventType = FindingEventType.Resolved,
                OccurredUtc = LaterUtc,
                Actor = "scanner"
            };
            using (ScannerDbContext context = _database.CreateContext())
            {
                FindingRepository repository = new FindingRepository(context);

                await repository.SaveChangesAsync(Changes(newEvents: new FindingEvent[] { resolvedEvent }), CancellationToken.None);
            }

            using (ScannerDbContext context = _database.CreateContext())
            {
                FindingEvent saved = context.FindingEvents.Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(saved.FindingId, Is.EqualTo(findingId));
                    Assert.That(saved.EventType, Is.EqualTo(FindingEventType.Resolved));
                }
            }
        }

        // One SaveChanges, so one transaction: if any part of a scan's changes fails, none is kept,
        // and the queue message is retried (ADR-013)
        [Test]
        public async Task SaveChangesAsync_WhenAnEventCannotBeSaved_SavesNothing()
        {
            int repositoryId = SeedRepository(TestEntities.NewFinding("a1"));
            DateTime originalLastSeenUtc = TestEntities.StartedUtc;

            using (ScannerDbContext context = _database.CreateContext(new FailingInsertInterceptor("FindingEvents")))
            {
                FindingRepository repository = new FindingRepository(context);
                Finding existing = (await repository.GetByRepositoryAsync(repositoryId, CancellationToken.None)).Single();
                existing.LastSeenUtc = LaterUtc;
                Finding added = TestEntities.NewFinding("b1");
                added.RepositoryId = repositoryId;
                FindingEvent seenEvent = new FindingEvent
                {
                    FindingId = existing.Id,
                    EventType = FindingEventType.Reopened,
                    OccurredUtc = LaterUtc,
                    Actor = "scanner"
                };
                FindingChanges changes = Changes(new Finding[] { added }, new Finding[] { existing }, new FindingEvent[] { seenEvent });

                await Assert.ThatAsync(() => repository.SaveChangesAsync(changes, CancellationToken.None), Throws.Exception);
            }

            using (ScannerDbContext context = _database.CreateContext())
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(context.Findings.Select(finding => finding.Fingerprint), Is.EquivalentTo(new string[] { "a1" }));
                    Assert.That(context.Findings.Single().LastSeenUtc, Is.EqualTo(originalLastSeenUtc));
                    Assert.That(context.FindingEvents.Count(), Is.Zero);
                }
            }
        }

        private static FindingChanges Changes(
            IReadOnlyList<Finding>? newFindings = null,
            IReadOnlyList<Finding>? updatedFindings = null,
            IReadOnlyList<FindingEvent>? newEvents = null)
        {
            return new FindingChanges(
                newFindings ?? Array.Empty<Finding>(),
                updatedFindings ?? Array.Empty<Finding>(),
                newEvents ?? Array.Empty<FindingEvent>());
        }

        private int SeedRepository(params Finding[] findings)
        {
            using (ScannerDbContext context = _database.CreateContext())
            {
                Repository repository = TestEntities.NewRepository();
                foreach (Finding finding in findings)
                {
                    repository.Findings.Add(finding);
                }

                context.Repositories.Add(repository);
                context.SaveChanges();
                return repository.Id;
            }
        }
    }
}
