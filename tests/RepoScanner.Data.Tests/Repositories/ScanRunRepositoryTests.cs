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
    public class ScanRunRepositoryTests
    {
        private static readonly Guid AzureDevOpsId = new Guid("6f1c2b8e-3d4a-4e5f-9a0b-1c2d3e4f5a6b");
        private static readonly Guid CorrelationId = new Guid("0b7d2f4e-8c1a-4d3b-9e6f-2a5c7e9b1d3f");
        private static readonly DateTime StartedUtc = new DateTime(2026, 9, 25, 2, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime CompletedUtc = new DateTime(2026, 9, 25, 2, 6, 30, DateTimeKind.Utc);

        private static readonly RepositoryDetails Details =
            new RepositoryDetails(AzureDevOpsId, "Lumina", "RepoScanner", "refs/heads/main");

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

        // The first scan of a repository is when the scanner first stores it
        [Test]
        public async Task StartRunAsync_ForANewRepository_AddsTheRepository()
        {
            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRunRepository repository = new ScanRunRepository(context);

                await repository.StartRunAsync(Details, ScanTrigger.Nightly, CorrelationId, StartedUtc, CancellationToken.None);
            }

            using (ScannerDbContext context = _database.CreateContext())
            {
                Repository saved = context.Repositories.Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(saved.AzureDevOpsId, Is.EqualTo(AzureDevOpsId));
                    Assert.That(saved.ProjectName, Is.EqualTo("Lumina"));
                    Assert.That(saved.Name, Is.EqualTo("RepoScanner"));
                    Assert.That(saved.DefaultBranch, Is.EqualTo("refs/heads/main"));
                }
            }
        }

        // A renamed repository is matched on its Azure DevOps ID, so it keeps its history
        // rather than starting again as a second repository
        [Test]
        public async Task StartRunAsync_ForARepositoryAlreadyStored_UpdatesItInsteadOfAddingAnother()
        {
            int repositoryId = SeedRepository(new Repository
            {
                AzureDevOpsId = AzureDevOpsId,
                ProjectName = "OldProject",
                Name = "OldName",
                DefaultBranch = "refs/heads/master"
            });

            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRunRepository repository = new ScanRunRepository(context);

                await repository.StartRunAsync(Details, ScanTrigger.Nightly, CorrelationId, StartedUtc, CancellationToken.None);
            }

            using (ScannerDbContext context = _database.CreateContext())
            {
                Repository saved = context.Repositories.Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(saved.Id, Is.EqualTo(repositoryId));
                    Assert.That(saved.ProjectName, Is.EqualTo("Lumina"));
                    Assert.That(saved.Name, Is.EqualTo("RepoScanner"));
                    Assert.That(saved.DefaultBranch, Is.EqualTo("refs/heads/main"));
                    Assert.That(context.ScanRuns.Single().RepositoryId, Is.EqualTo(repositoryId));
                }
            }
        }

        [Test]
        public async Task StartRunAsync_AddsARunningRunWithTheGivenDetails()
        {
            ScanRun returned;
            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRunRepository repository = new ScanRunRepository(context);

                returned = await repository.StartRunAsync(Details, ScanTrigger.OnDemand, CorrelationId, StartedUtc, CancellationToken.None);
            }

            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun saved = context.ScanRuns.Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(returned.Id, Is.EqualTo(saved.Id));
                    Assert.That(returned.RepositoryId, Is.EqualTo(context.Repositories.Single().Id));
                    Assert.That(saved.TriggerType, Is.EqualTo(ScanTrigger.OnDemand));
                    Assert.That(saved.Status, Is.EqualTo(ScanRunStatus.Running));
                    Assert.That(saved.CorrelationId, Is.EqualTo(CorrelationId));
                    Assert.That(saved.StartedUtc, Is.EqualTo(StartedUtc));
                    Assert.That(saved.CompletedUtc, Is.Null);
                    Assert.That(saved.CommitSha, Is.Null);
                }
            }
        }

        // One SaveChanges, so one transaction: a repository is never stored without the run that found it
        [Test]
        public async Task StartRunAsync_WhenTheRunCannotBeSaved_DoesNotSaveTheRepositoryEither()
        {
            using (ScannerDbContext context = _database.CreateContext(new FailingInsertInterceptor("ScanRuns")))
            {
                ScanRunRepository repository = new ScanRunRepository(context);

                await Assert.ThatAsync(
                    () => repository.StartRunAsync(Details, ScanTrigger.Nightly, CorrelationId, StartedUtc, CancellationToken.None),
                    Throws.Exception);
            }

            using (ScannerDbContext context = _database.CreateContext())
            {
                Assert.That(context.Repositories.Count(), Is.Zero);
            }
        }

        [Test]
        public async Task CompleteRunAsync_StoresTheFinalStatusCommitCountsAndError()
        {
            int scanRunId = SeedRunningRun();
            ScanRunCompletion completion = new ScanRunCompletion(
                ScanRunStatus.PartiallyCompleted, "9fceb02d0ae598e95dc970b74767f19372d61af8", CompletedUtc,
                NewCount: 3, ReopenedCount: 1, ResolvedCount: 2, "Package scanner failed", new List<ScannerResult>());

            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRunRepository repository = new ScanRunRepository(context);

                await repository.CompleteRunAsync(scanRunId, completion, CancellationToken.None);
            }

            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun saved = context.ScanRuns.Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(saved.Status, Is.EqualTo(ScanRunStatus.PartiallyCompleted));
                    Assert.That(saved.CommitSha, Is.EqualTo("9fceb02d0ae598e95dc970b74767f19372d61af8"));
                    Assert.That(saved.CompletedUtc, Is.EqualTo(CompletedUtc));
                    Assert.That(saved.NewCount, Is.EqualTo(3));
                    Assert.That(saved.ReopenedCount, Is.EqualTo(1));
                    Assert.That(saved.ResolvedCount, Is.EqualTo(2));
                    Assert.That(saved.ErrorMessage, Is.EqualTo("Package scanner failed"));
                }
            }
        }

        // Task #25 reads these to know which scanner failed
        [Test]
        public async Task CompleteRunAsync_AddsTheResultOfEachScanner()
        {
            int scanRunId = SeedRunningRun();
            List<ScannerResult> results = new List<ScannerResult>
            {
                new ScannerResult { ScannerType = ScannerType.Secret, Status = ScannerStatus.Succeeded, FindingCount = 2, DurationMs = 1500 },
                new ScannerResult { ScannerType = ScannerType.Package, Status = ScannerStatus.Failed, DurationMs = 800, ErrorMessage = "Index unreachable" }
            };
            ScanRunCompletion completion = new ScanRunCompletion(
                ScanRunStatus.PartiallyCompleted, null, CompletedUtc, 0, 0, 0, null, results);

            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRunRepository repository = new ScanRunRepository(context);

                await repository.CompleteRunAsync(scanRunId, completion, CancellationToken.None);
            }

            using (ScannerDbContext context = _database.CreateContext())
            {
                List<ScannerResult> saved = context.ScannerResults.Where(result => result.ScanRunId == scanRunId).ToList();
                ScannerResult secret = saved.Single(result => result.ScannerType == ScannerType.Secret);
                ScannerResult package = saved.Single(result => result.ScannerType == ScannerType.Package);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(saved, Has.Count.EqualTo(2));
                    Assert.That(secret.Status, Is.EqualTo(ScannerStatus.Succeeded));
                    Assert.That(secret.FindingCount, Is.EqualTo(2));
                    Assert.That(secret.DurationMs, Is.EqualTo(1500));
                    Assert.That(package.Status, Is.EqualTo(ScannerStatus.Failed));
                    Assert.That(package.ErrorMessage, Is.EqualTo("Index unreachable"));
                }
            }
        }

        // ADR-013: a half-saved completion must never happen, so a run whose results cannot be
        // saved stays exactly as it was
        [Test]
        public async Task CompleteRunAsync_WhenAResultCannotBeSaved_LeavesTheRunUnchanged()
        {
            int scanRunId = SeedRunningRun();
            List<ScannerResult> results = new List<ScannerResult>
            {
                new ScannerResult { ScannerType = ScannerType.Secret, Status = ScannerStatus.Succeeded }
            };
            ScanRunCompletion completion = new ScanRunCompletion(
                ScanRunStatus.Completed, null, CompletedUtc, 0, 0, 0, null, results);

            using (ScannerDbContext context = _database.CreateContext(new FailingInsertInterceptor("ScannerResults")))
            {
                ScanRunRepository repository = new ScanRunRepository(context);

                await Assert.ThatAsync(
                    () => repository.CompleteRunAsync(scanRunId, completion, CancellationToken.None),
                    Throws.Exception);
            }

            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun saved = context.ScanRuns.Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(saved.Status, Is.EqualTo(ScanRunStatus.Running));
                    Assert.That(saved.CompletedUtc, Is.Null);
                    Assert.That(context.ScannerResults.Count(), Is.Zero);
                }
            }
        }

        // #33: "is this repository being scanned right now?" Only Running rows, only of that repository, oldest first
        [Test]
        public async Task GetRunningAsync_ReturnsOnlyThatRepositorysRunningRunsOldestFirst()
        {
            Repository scanned = new Repository { AzureDevOpsId = AzureDevOpsId, ProjectName = "Lumina", Name = "RepoScanner", DefaultBranch = "main" };
            ScanRun newer = RunStarted(StartedUtc.AddMinutes(30), ScanRunStatus.Running);
            ScanRun older = RunStarted(StartedUtc, ScanRunStatus.Running);
            scanned.ScanRuns.Add(newer);
            scanned.ScanRuns.Add(older);
            scanned.ScanRuns.Add(RunStarted(StartedUtc, ScanRunStatus.Completed));
            scanned.ScanRuns.Add(RunStarted(StartedUtc, ScanRunStatus.Failed));
            scanned.ScanRuns.Add(RunStarted(StartedUtc, ScanRunStatus.PartiallyCompleted));
            SeedRepository(scanned);

            Repository other = TestEntities.NewRepository();
            other.ScanRuns.Add(RunStarted(StartedUtc, ScanRunStatus.Running));
            SeedRepository(other);

            using (ScannerDbContext context = _database.CreateContext())
            {
                IReadOnlyList<ScanRun> running = await new ScanRunRepository(context).GetRunningAsync(AzureDevOpsId, CancellationToken.None);

                Assert.That(running.Select(run => run.Id), Is.EqualTo(new[] { older.Id, newer.Id }));
            }
        }

        [Test]
        public async Task GetRunningAsync_ForARepositoryNotStoredYet_ReturnsNone()
        {
            using (ScannerDbContext context = _database.CreateContext())
            {
                IReadOnlyList<ScanRun> running = await new ScanRunRepository(context).GetRunningAsync(AzureDevOpsId, CancellationToken.None);

                Assert.That(running, Is.Empty);
            }
        }

        private static ScanRun RunStarted(DateTime startedUtc, ScanRunStatus status)
        {
            ScanRun scanRun = TestEntities.NewScanRun();
            scanRun.StartedUtc = startedUtc;
            scanRun.Status = status;
            return scanRun;
        }

        private int SeedRepository(Repository repository)
        {
            using (ScannerDbContext context = _database.CreateContext())
            {
                context.Repositories.Add(repository);
                context.SaveChanges();
                return repository.Id;
            }
        }

        private int SeedRunningRun()
        {
            using (ScannerDbContext context = _database.CreateContext())
            {
                Repository repository = TestEntities.NewRepository();
                ScanRun scanRun = TestEntities.NewScanRun();
                repository.ScanRuns.Add(scanRun);
                context.Repositories.Add(repository);
                context.SaveChanges();
                return scanRun.Id;
            }
        }
    }
}
