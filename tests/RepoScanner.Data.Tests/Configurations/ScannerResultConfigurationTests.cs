using System.Linq;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using RepoScanner.Core.Entities;
using RepoScanner.Core.Enums;

namespace RepoScanner.Data.Tests.Configurations
{
    [TestFixture]
    public class ScannerResultConfigurationTests
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
            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun scanRun = SaveScanRun(context);
                scanRun.ScannerResults.Add(new ScannerResult
                {
                    ScannerType = ScannerType.Package,
                    Status = ScannerStatus.Failed,
                    FindingCount = 4,
                    DurationMs = 12500,
                    ErrorMessage = "nuget.org vulnerability index unreachable"
                });
                context.SaveChanges();
            }

            using (ScannerDbContext context = _database.CreateContext())
            {
                ScannerResult saved = context.ScannerResults.Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(saved.Id, Is.GreaterThan(0));
                    Assert.That(saved.ScanRunId, Is.EqualTo(context.ScanRuns.Single().Id));
                    Assert.That(saved.ScannerType, Is.EqualTo(ScannerType.Package));
                    Assert.That(saved.Status, Is.EqualTo(ScannerStatus.Failed));
                    Assert.That(saved.FindingCount, Is.EqualTo(4));
                    Assert.That(saved.DurationMs, Is.EqualTo(12500));
                    Assert.That(saved.ErrorMessage, Is.EqualTo("nuget.org vulnerability index unreachable"));
                }
            }
        }

        // A scanner that succeeded has no error message
        [Test]
        public void Save_WithOnlyRequiredColumns_IsAccepted()
        {
            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun scanRun = SaveScanRun(context);
                scanRun.ScannerResults.Add(new ScannerResult { ScannerType = ScannerType.Secret, Status = ScannerStatus.Succeeded });

                Assert.That(() => context.SaveChanges(), Throws.Nothing);
            }
        }

        // Each scanner reports once per run; a second row would make task #25 read two different outcomes
        [Test]
        public void Save_WithASecondResultForTheSameScannerInOneRun_IsRejected()
        {
            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun scanRun = SaveScanRun(context);
                scanRun.ScannerResults.Add(new ScannerResult { ScannerType = ScannerType.Secret });
                scanRun.ScannerResults.Add(new ScannerResult { ScannerType = ScannerType.Secret });

                Assert.That(() => context.SaveChanges(), Throws.TypeOf<DbUpdateException>());
            }
        }

        // The rule is one result per scanner per run, not one per scanner overall
        [Test]
        public void Save_WithTheSameScannerInTwoRuns_IsAccepted()
        {
            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun firstRun = SaveScanRun(context);
                ScanRun secondRun = SaveScanRun(context);
                firstRun.ScannerResults.Add(new ScannerResult { ScannerType = ScannerType.Secret });
                secondRun.ScannerResults.Add(new ScannerResult { ScannerType = ScannerType.Secret });

                Assert.That(() => context.SaveChanges(), Throws.Nothing);
            }
        }

        // Enums are stored as their names, so the SQL reads 'CodePattern', not 2
        [Test]
        public void Save_StoresTheEnumsAsText()
        {
            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun scanRun = SaveScanRun(context);
                scanRun.ScannerResults.Add(new ScannerResult { ScannerType = ScannerType.CodePattern, Status = ScannerStatus.Failed });
                context.SaveChanges();
            }

            object? stored = _database.ExecuteScalar("SELECT ScannerType || '/' || Status FROM ScannerResults");

            Assert.That(stored, Is.EqualTo("CodePattern/Failed"));
        }

        private static ScanRun SaveScanRun(ScannerDbContext context)
        {
            Repository repository = TestEntities.NewRepository();
            ScanRun scanRun = TestEntities.NewScanRun();
            repository.ScanRuns.Add(scanRun);
            context.Repositories.Add(repository);
            context.SaveChanges();
            return scanRun;
        }
    }
}
