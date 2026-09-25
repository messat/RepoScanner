using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Testing;
using NUnit.Framework;
using RepoScanner.Core.Scanning;
using RepoScanner.Scanner.Files;
using RepoScanner.Scanner.Secrets;

namespace RepoScanner.Scanner.Tests.Secrets
{
    [TestFixture]
    public class FixtureScanTests
    {
        private FakeLogger<SecretScanner> _scannerLogger = null!;
        private FakeLogger<RepositoryFileWalker> _walkerLogger = null!;
        private IReadOnlyList<RawFinding> _findings = null!;

        [SetUp]
        public async Task SetUp()
        {
            _scannerLogger = new FakeLogger<SecretScanner>();
            _walkerLogger = new FakeLogger<RepositoryFileWalker>();
            SecretScanner scanner = new SecretScanner(new RepositoryFileWalker(_walkerLogger), AllSecretRules.Create(), _scannerLogger);

            _findings = await scanner.ScanAsync(FixtureSecrets.FolderPath, CancellationToken.None);
        }

        // Issue 3 criterion 1, at the scanner: exactly the labelled secrets, each where its label says
        [Test]
        public void ScanAsync_OnTheFixture_FindsExactlyTheLabelledSecrets()
        {
            IEnumerable<(string, string, int?)> found = _findings.Select(finding => (finding.RuleId, finding.FilePath, finding.LineNumber));
            IEnumerable<(string, string, int?)> labelled = FixtureSecrets.All.Select(secret => (secret.RuleId, secret.FilePath, (int?)secret.LineNumber));

            Assert.That(found, Is.EqualTo(labelled));
        }

        [Test]
        public void ScanAsync_OnTheFixture_MasksAndHashesEachSecret()
        {
            Assert.That(_findings, Has.Count.EqualTo(FixtureSecrets.All.Count));

            for (int index = 0; index < _findings.Count; index++)
            {
                FixtureSecret secret = FixtureSecrets.All[index];
                string label = secret.RuleId + " in " + secret.FilePath;

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(_findings[index].MaskedPreview, Is.EqualTo(SecretMasker.Mask(secret.Value)), label);
                    Assert.That(_findings[index].SecretHash, Is.EqualTo(SecretHasher.Hash(secret.Value)), label);
                }
            }
        }

        // #18 and Issue 3 criterion 2: no raw value in a finding. A record's ToString lists every property.
        [Test]
        public void ScanAsync_OnTheFixture_PutsNoRawValueInAFinding()
        {
            string findingsText = string.Join("\n", _findings.Select(finding => finding.ToString()));

            AssertNoValueLeaked(findingsText, "a finding");
        }

        // #18 and Issue 3 criterion 2: no raw value in a log line
        [Test]
        public void ScanAsync_OnTheFixture_PutsNoRawValueInALogLine()
        {
            IEnumerable<FakeLogRecord> records = _scannerLogger.Collector.GetSnapshot().Concat(_walkerLogger.Collector.GetSnapshot());
            string logText = string.Join("\n", records.Select(record => record.Message + " " + record.Exception));

            AssertNoValueLeaked(logText, "the log");
        }

        [Test]
        public void ScanAsync_OnTheFixture_SkipsTheToolFolderAndTheMarkedFolder()
        {
            FakeLogRecord summary = _walkerLogger.LatestRecord;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(summary.GetStructuredStateValue("FolderCount"), Is.EqualTo("1"), "node_modules");
                Assert.That(summary.GetStructuredStateValue("ExcludedCount"), Is.EqualTo("1"), "vendor");
            }
        }

        // Is.False with a message, not Does.Not.Contain: a failure must not print the value itself
        private static void AssertNoValueLeaked(string text, string place)
        {
            foreach (FixtureSecret secret in FixtureSecrets.All)
            {
                bool leaked = text.Contains(secret.Value, StringComparison.Ordinal);

                Assert.That(leaked, Is.False, secret.RuleId + " value from " + secret.FilePath + " leaked into " + place);
            }
        }
    }
}
