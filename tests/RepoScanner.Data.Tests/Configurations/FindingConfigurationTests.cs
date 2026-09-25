using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using RepoScanner.Core.Entities;
using RepoScanner.Core.Enums;

namespace RepoScanner.Data.Tests.Configurations
{
    [TestFixture]
    public class FindingConfigurationTests
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

        // Every column set, including a valid suppression (Suppressed with a reason)
        [Test]
        public void SaveAndReload_KeepsEveryColumn()
        {
            DateTime firstSeenUtc = new DateTime(2026, 9, 20, 2, 1, 0, DateTimeKind.Utc);
            DateTime lastSeenUtc = new DateTime(2026, 9, 24, 2, 3, 0, DateTimeKind.Utc);
            DateTime resolvedUtc = new DateTime(2026, 9, 22, 2, 2, 0, DateTimeKind.Utc);
            DateTime suppressedUntilUtc = new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc);
            using (ScannerDbContext context = _database.CreateContext())
            {
                Repository repository = TestEntities.NewRepository();
                repository.Findings.Add(new Finding
                {
                    Fingerprint = "3f79bb7b435b05321651daefd374cdc681dc06faa65e374e38337b88ca046dea",
                    ScannerType = ScannerType.Package,
                    RuleId = "PKG001",
                    Severity = Severity.High,
                    FilePath = "src/RepoScanner.Scanner/RepoScanner.Scanner.csproj",
                    LineNumber = 14,
                    Message = "System.Text.Json 8.0.0 has a known vulnerability",
                    MaskedPreview = "Pass****",
                    SecretHash = "5e884898da28047151d0e56f8dc6292773603d0d6aabbdd62a11ef721d1542d8",
                    PackageName = "System.Text.Json",
                    PackageVersion = "8.0.0",
                    AdvisoryUrl = "https://github.com/advisories/GHSA-8g4q-xg66-9fp4",
                    Status = FindingStatus.Suppressed,
                    FirstSeenUtc = firstSeenUtc,
                    LastSeenUtc = lastSeenUtc,
                    ResolvedUtc = resolvedUtc,
                    SuppressedUntilUtc = suppressedUntilUtc,
                    SuppressedBy = "developer@example.com",
                    SuppressionReason = "Test fixture, not a real package reference"
                });
                context.Repositories.Add(repository);
                context.SaveChanges();
            }

            using (ScannerDbContext context = _database.CreateContext())
            {
                Finding saved = context.Findings.Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(saved.Id, Is.GreaterThan(0));
                    Assert.That(saved.RepositoryId, Is.EqualTo(context.Repositories.Single().Id));
                    Assert.That(saved.Fingerprint, Is.EqualTo("3f79bb7b435b05321651daefd374cdc681dc06faa65e374e38337b88ca046dea"));
                    Assert.That(saved.ScannerType, Is.EqualTo(ScannerType.Package));
                    Assert.That(saved.RuleId, Is.EqualTo("PKG001"));
                    Assert.That(saved.Severity, Is.EqualTo(Severity.High));
                    Assert.That(saved.FilePath, Is.EqualTo("src/RepoScanner.Scanner/RepoScanner.Scanner.csproj"));
                    Assert.That(saved.LineNumber, Is.EqualTo(14));
                    Assert.That(saved.Message, Is.EqualTo("System.Text.Json 8.0.0 has a known vulnerability"));
                    Assert.That(saved.MaskedPreview, Is.EqualTo("Pass****"));
                    Assert.That(saved.SecretHash, Is.EqualTo("5e884898da28047151d0e56f8dc6292773603d0d6aabbdd62a11ef721d1542d8"));
                    Assert.That(saved.PackageName, Is.EqualTo("System.Text.Json"));
                    Assert.That(saved.PackageVersion, Is.EqualTo("8.0.0"));
                    Assert.That(saved.AdvisoryUrl, Is.EqualTo("https://github.com/advisories/GHSA-8g4q-xg66-9fp4"));
                    Assert.That(saved.Status, Is.EqualTo(FindingStatus.Suppressed));
                    Assert.That(saved.FirstSeenUtc, Is.EqualTo(firstSeenUtc));
                    Assert.That(saved.LastSeenUtc, Is.EqualTo(lastSeenUtc));
                    Assert.That(saved.ResolvedUtc, Is.EqualTo(resolvedUtc));
                    Assert.That(saved.SuppressedUntilUtc, Is.EqualTo(suppressedUntilUtc));
                    Assert.That(saved.SuppressedBy, Is.EqualTo("developer@example.com"));
                    Assert.That(saved.SuppressionReason, Is.EqualTo("Test fixture, not a real package reference"));
                }
            }
        }

        // A code-pattern finding has no secret or package columns
        [Test]
        public void Save_WithOnlyRequiredColumns_IsAccepted()
        {
            using (ScannerDbContext context = _database.CreateContext())
            {
                Repository repository = TestEntities.NewRepository();
                repository.Findings.Add(TestEntities.NewFinding("a1"));
                context.Repositories.Add(repository);

                Assert.That(() => context.SaveChanges(), Throws.Nothing);
            }
        }

        // ADR-004: the queue delivers at least once, so a repeated message must never create a second
        // row for the same finding
        [Test]
        public void Save_WithAFingerprintAlreadyStoredForTheRepository_IsRejected()
        {
            using (ScannerDbContext context = _database.CreateContext())
            {
                Repository repository = TestEntities.NewRepository();
                repository.Findings.Add(TestEntities.NewFinding("a1"));
                repository.Findings.Add(TestEntities.NewFinding("a1"));
                context.Repositories.Add(repository);

                Assert.That(() => context.SaveChanges(), Throws.TypeOf<DbUpdateException>());
            }
        }

        // The same secret committed to two repositories is two findings: uniqueness is per repository
        [Test]
        public void Save_WithTheSameFingerprintInTwoRepositories_IsAccepted()
        {
            using (ScannerDbContext context = _database.CreateContext())
            {
                Repository first = TestEntities.NewRepository();
                Repository second = TestEntities.NewRepository();
                first.Findings.Add(TestEntities.NewFinding("a1"));
                second.Findings.Add(TestEntities.NewFinding("a1"));
                context.Repositories.AddRange(first, second);

                Assert.That(() => context.SaveChanges(), Throws.Nothing);
            }
        }

        // ADR-019: a suppression must have a reason, whichever application writes it
        [Test]
        public void Save_OfASuppressedFindingWithoutAReason_IsRejected()
        {
            using (ScannerDbContext context = _database.CreateContext())
            {
                Repository repository = TestEntities.NewRepository();
                Finding finding = TestEntities.NewFinding("a1");
                finding.Status = FindingStatus.Suppressed;
                finding.SuppressionReason = null;
                repository.Findings.Add(finding);
                context.Repositories.Add(repository);

                Assert.That(() => context.SaveChanges(), Throws.TypeOf<DbUpdateException>());
            }
        }

        // Enums are stored as their names, so the SQL reads 'Open', not 0
        [Test]
        public void Save_StoresTheEnumsAsText()
        {
            using (ScannerDbContext context = _database.CreateContext())
            {
                Repository repository = TestEntities.NewRepository();
                Finding finding = TestEntities.NewFinding("a1");
                finding.ScannerType = ScannerType.Package;
                finding.Severity = Severity.High;
                repository.Findings.Add(finding);
                context.Repositories.Add(repository);
                context.SaveChanges();
            }

            object? stored = _database.ExecuteScalar("SELECT ScannerType || '/' || Severity || '/' || Status FROM Findings");

            Assert.That(stored, Is.EqualTo("Package/High/Open"));
        }
    }
}
