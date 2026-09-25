using NUnit.Framework;
using RepoScanner.Core.Entities;

namespace RepoScanner.Core.Tests.Entities
{
    [TestFixture]
    public class RepositoryTests
    {
        // The scanner adds to these on a repository it has just created, before EF Core has loaded anything
        [Test]
        public void ScanRuns_OnANewRepository_AcceptsAScanRun()
        {
            Repository repository = new Repository();

            repository.ScanRuns.Add(new ScanRun());

            Assert.That(repository.ScanRuns, Has.Count.EqualTo(1));
        }

        [Test]
        public void Findings_OnANewRepository_AcceptsAFinding()
        {
            Repository repository = new Repository();

            repository.Findings.Add(new Finding());

            Assert.That(repository.Findings, Has.Count.EqualTo(1));
        }
    }
}
