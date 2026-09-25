using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using RepoScanner.Core.Entities;

namespace RepoScanner.Data.Tests.Configurations
{
    [TestFixture]
    public class RepositoryConfigurationTests
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
            Guid azureDevOpsId = new Guid("6f1c2b8e-3d4a-4e5f-9a0b-1c2d3e4f5a6b");
            using (ScannerDbContext context = _database.CreateContext())
            {
                context.Repositories.Add(new Repository
                {
                    AzureDevOpsId = azureDevOpsId,
                    ProjectName = "Lumina",
                    Name = "RepoScanner",
                    DefaultBranch = "refs/heads/main"
                });
                context.SaveChanges();
            }

            using (ScannerDbContext context = _database.CreateContext())
            {
                Repository saved = context.Repositories.Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(saved.Id, Is.GreaterThan(0));
                    Assert.That(saved.AzureDevOpsId, Is.EqualTo(azureDevOpsId));
                    Assert.That(saved.ProjectName, Is.EqualTo("Lumina"));
                    Assert.That(saved.Name, Is.EqualTo("RepoScanner"));
                    Assert.That(saved.DefaultBranch, Is.EqualTo("refs/heads/main"));
                }
            }
        }

        // The scanner matches repositories on their Azure DevOps ID; a second row would split one
        // repository's history in two
        [Test]
        public void Save_WithAnAzureDevOpsIdAlreadyStored_IsRejected()
        {
            Guid azureDevOpsId = new Guid("6f1c2b8e-3d4a-4e5f-9a0b-1c2d3e4f5a6b");
            using (ScannerDbContext context = _database.CreateContext())
            {
                Repository repository = TestEntities.NewRepository();
                repository.AzureDevOpsId = azureDevOpsId;
                context.Repositories.Add(repository);
                context.SaveChanges();
            }

            using (ScannerDbContext context = _database.CreateContext())
            {
                Repository duplicate = TestEntities.NewRepository();
                duplicate.AzureDevOpsId = azureDevOpsId;
                context.Repositories.Add(duplicate);

                Assert.That(() => context.SaveChanges(), Throws.TypeOf<DbUpdateException>());
            }
        }

        // ADR-019: nothing is deleted, so deleting a repository must never quietly delete its findings
        [Test]
        public void Delete_OfARepositoryThatHasFindings_IsRejected()
        {
            int repositoryId;
            using (ScannerDbContext context = _database.CreateContext())
            {
                Repository repository = TestEntities.NewRepository();
                repository.Findings.Add(TestEntities.NewFinding("a1"));
                context.Repositories.Add(repository);
                context.SaveChanges();
                repositoryId = repository.Id;
            }

            // The findings are not loaded, so only the database's foreign key can stop the delete
            using (ScannerDbContext context = _database.CreateContext())
            {
                Repository repository = context.Repositories.Single(saved => saved.Id == repositoryId);
                context.Repositories.Remove(repository);

                Assert.That(() => context.SaveChanges(), Throws.TypeOf<DbUpdateException>());
            }
        }
    }
}
