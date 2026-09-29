using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RepoScanner.Core.Enums;
using RepoScanner.Core.Scanning;
using RepoScanner.Scanner.CodePatterns;
using RepoScanner.Scanner.Files;

namespace RepoScanner.Scanner.Tests.CodePatterns
{
    // All six rules together over tests/fixtures/patterns-repo (#54)
    [TestFixture]
    public class FixturePatternScanTests
    {
        private IReadOnlyList<RawFinding> _findings = null!;

        [OneTimeSetUp]
        public async Task ScanTheFixture()
        {
            CodePatternScanner scanner = new CodePatternScanner(
                new RepositoryFileWalker(NullLogger<RepositoryFileWalker>.Instance), AllCodePatternRules.Create(), NullLogger<CodePatternScanner>.Instance);

            _findings = await scanner.ScanAsync(FixturePatterns.FolderPath, CancellationToken.None);
        }

        // Issue 6 criterion 2: exactly the planted findings, each where its marker is, and no others. That
        // includes nothing from Quiet/ and none of the cases next to each finding that a rule must not report.
        [Test]
        public void ScanAsync_OnTheFixture_FindsExactlyThePlantedPatterns()
        {
            IEnumerable<(string, string, int?)> found = _findings.Select(finding => (finding.RuleId, finding.FilePath, finding.LineNumber));
            IEnumerable<(string, string, int?)> planted = FixturePatterns.All.Select(pattern => (pattern.RuleId, pattern.FilePath, (int?)pattern.LineNumber));

            Assert.That(found, Is.EqualTo(planted));
        }

        // Issue 6 criterion 1 for the fixture: every rule is shown working at least once
        [Test]
        public void ScanAsync_OnTheFixture_ReportsEveryRule()
        {
            Assert.That(_findings.Select(finding => finding.RuleId).Distinct(),
                Is.EquivalentTo(new string[] { "CP001", "CP002", "CP003", "CP004", "CP005", "CP006" }));
        }

        // Each finding can be saved: the parts of its fingerprint are there, and the two empty catches of one
        // file, which look the same, still get fingerprints of their own (ADR-024)
        [Test]
        public void ScanAsync_OnTheFixture_GivesEachFindingItsOwnSnippetHash()
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_findings.Select(finding => finding.ScannerType), Is.All.EqualTo(ScannerType.CodePattern));
                Assert.That(_findings.Select(finding => finding.SnippetHash).Distinct().Count(), Is.EqualTo(FixturePatterns.All.Count));
            }
        }
    }
}
