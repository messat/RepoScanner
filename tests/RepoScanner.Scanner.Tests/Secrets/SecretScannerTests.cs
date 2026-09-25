using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using NUnit.Framework;
using RepoScanner.Core.Enums;
using RepoScanner.Core.Scanning;
using RepoScanner.Scanner.Files;
using RepoScanner.Scanner.Secrets;
using RepoScanner.Scanner.Secrets.Rules;

namespace RepoScanner.Scanner.Tests.Secrets
{
    [TestFixture]
    public class SecretScannerTests
    {
        private const string NuGetKey = "oy2fakenugetapikeyforreposcannertests000000000";
        private const string ClientSecret = "FAK8Q~FAKE.client_secret~for-RepoScanner";

        private TempFolder _folder = null!;
        private FakeLogger<SecretScanner> _logger = null!;

        [SetUp]
        public void SetUp()
        {
            _folder = new TempFolder();
            _logger = new FakeLogger<SecretScanner>();
        }

        [TearDown]
        public void TearDown()
        {
            _folder.Dispose();
        }

        [Test]
        public void ScannerType_IsSecret()
        {
            Assert.That(CreateScanner().ScannerType, Is.EqualTo(ScannerType.Secret));
        }

        [Test]
        public async Task ScanAsync_ReportsTheRuleAndWhereTheSecretIs()
        {
            _folder.WriteText("deploy/publish.ps1", "# Publishes the package\n\ndotnet nuget push out.nupkg --api-key " + NuGetKey + "\n");

            RawFinding finding = (await ScanAsync()).Single();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(finding.ScannerType, Is.EqualTo(ScannerType.Secret));
                Assert.That(finding.RuleId, Is.EqualTo("SEC006"));
                Assert.That(finding.Severity, Is.EqualTo(Severity.High));
                Assert.That(finding.Message, Is.EqualTo("NuGet API key"));
                Assert.That(finding.FilePath, Is.EqualTo("deploy/publish.ps1"));
                Assert.That(finding.LineNumber, Is.EqualTo(3));
            }
        }

        [Test]
        public async Task ScanAsync_MasksAndHashesTheSecret()
        {
            _folder.WriteText("publish.ps1", "--api-key " + NuGetKey);

            RawFinding finding = (await ScanAsync()).Single();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(finding.MaskedPreview, Is.EqualTo("oy2f****"));
                Assert.That(finding.SecretHash, Is.EqualTo(SecretHasher.Hash(NuGetKey)));
            }
        }

        // Review focus: git on Windows usually checks files out with CRLF line endings, and that
        // must not change a finding's line or its hash
        [Test]
        public async Task ScanAsync_WithWindowsLineEndings_ReportsTheSameLinesAndHashes()
        {
            string content = "# settings\n"
                + "-----BEGIN RSA PRIVATE KEY-----\n"
                + "MIIEFAKEprivateKeyForRepoScannerTestsAAAAAAAAAAAAAAAAAAAAAAAAAAA\n"
                + "FAKEsecondLineOfTheKeyBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB\n"
                + "-----END RSA PRIVATE KEY-----\n"
                + "--api-key " + NuGetKey + "\n";
            _folder.WriteText("lf/key.txt", content);
            _folder.WriteText("crlf/key.txt", content.Replace("\n", "\r\n"));

            IReadOnlyList<RawFinding> findings = await ScanAsync();
            List<(string RuleId, int? LineNumber, string? SecretHash)> lf = FindingsIn(findings, "lf/");
            List<(string RuleId, int? LineNumber, string? SecretHash)> crlf = FindingsIn(findings, "crlf/");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(lf.Select(finding => finding.LineNumber), Is.EqualTo(new int?[] { 2, 6 }));
                Assert.That(crlf, Is.EqualTo(lf));
            }
        }

        // Both SEC005 and SEC007 match a client secret held in a setting named ...Secret. It is
        // reported once, by the lower-numbered, more specific rule.
        [Test]
        public async Task ScanAsync_ReportsAnOverlapOnceUnderTheLowerNumberedRule()
        {
            _folder.WriteText("Settings.cs", "public const string GraphClientSecret = \"" + ClientSecret + "\";\n");

            IReadOnlyList<RawFinding> findings = await ScanAsync();

            Assert.That(findings.Select(finding => finding.RuleId), Is.EqualTo(new string[] { "SEC005" }));
        }

        // The rules can be given in any order; they still run in ID order, so the specific rule wins
        [Test]
        public async Task ScanAsync_RunsTheRulesInIdOrderWhateverOrderTheyAreGivenIn()
        {
            _folder.WriteText("Settings.cs", "public const string GraphClientSecret = \"" + ClientSecret + "\";\n");
            SecretScanner scanner = new SecretScanner(Walker(), new ISecretRule[] { new Sec007NamedSettingRule(), new Sec005EntraClientSecretRule() }, _logger);

            IReadOnlyList<RawFinding> findings = await scanner.ScanAsync(_folder.FullPath, CancellationToken.None);

            Assert.That(findings.Select(finding => finding.RuleId), Is.EqualTo(new string[] { "SEC005" }));
        }

        [Test]
        public async Task ScanAsync_IgnoresPlaceholdersAndLogsHowMany()
        {
            _folder.WriteText("appsettings.json", "{\n  \"Password\": \"changeme\",\n  \"ApiKey\": \"#{ApiKey}#\"\n}\n");

            IReadOnlyList<RawFinding> findings = await ScanAsync();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(findings, Is.Empty);
                Assert.That(_logger.LatestRecord.GetStructuredStateValue("PlaceholderCount"), Is.EqualTo("2"));
            }
        }

        [Test]
        public async Task ScanAsync_ReturnsFindingsSortedByFileThenLine()
        {
            _folder.WriteText("b.ps1", "--api-key " + NuGetKey + "\n");
            _folder.WriteText("a.ps1", "\n\n--api-key " + NuGetKey + "\n# " + ClientSecret + "\n");

            IReadOnlyList<RawFinding> findings = await ScanAsync();

            Assert.That(findings.Select(finding => (finding.FilePath, finding.LineNumber)),
                Is.EqualTo(new (string, int?)[] { ("a.ps1", 3), ("a.ps1", 4), ("b.ps1", 1) }));
        }

        // A pattern that times out on one file is skipped for that file only
        [Test]
        public async Task ScanAsync_WhenARuleTimesOut_LogsAWarningAndStillRunsTheOtherRules()
        {
            _folder.WriteText("slow.txt", new string('a', 40) + "!\n--api-key " + NuGetKey + "\n");
            SecretScanner scanner = new SecretScanner(Walker(), new ISecretRule[] { new SlowRule(), new Sec006NuGetApiKeyRule() }, _logger);

            IReadOnlyList<RawFinding> findings = await scanner.ScanAsync(_folder.FullPath, CancellationToken.None);

            FakeLogRecord warning = _logger.Collector.GetSnapshot().Single(record => record.Level == LogLevel.Warning);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(findings.Select(finding => finding.RuleId), Is.EqualTo(new string[] { "SEC006" }));
                Assert.That(warning.Message, Does.Contain("SEC000").And.Contain("slow.txt"));
                Assert.That(warning.Exception, Is.Null, "The timeout's Input property holds the file's text");
            }
        }

        // Review focus: an old file saved as Windows-1252 is not valid UTF-8, but is still scanned
        [Test]
        public async Task ScanAsync_ScansAFileThatIsNotValidUtf8()
        {
            // In Latin-1 and Windows-1252, "£" is the byte 0xA3, which is not valid UTF-8 on its own
            _folder.WriteBytes("legacy.ps1", Encoding.Latin1.GetBytes("# Price: £5\n--api-key " + NuGetKey + "\n"));

            RawFinding finding = (await ScanAsync()).Single();

            Assert.That(finding.LineNumber, Is.EqualTo(2));
        }

        // Review fix: a file that passes the walk but cannot be read in full (another program has locked
        // part of it after the first 8,000 bytes) is skipped with a warning, and the rest is still scanned.
        // Only Windows makes other readers respect such a lock.
        [Test]
        public async Task ScanAsync_WhenAFileCannotBeReadInFull_SkipsItWithAWarningAndScansTheRest()
        {
            if (OperatingSystem.IsWindows())
            {
                string lockedPath = _folder.WriteBytes("big.txt", Encoding.ASCII.GetBytes(new string('a', 20000)));
                _folder.WriteText("publish.ps1", "--api-key " + NuGetKey + "\n");
                FakeLogger<RepositoryFileWalker> walkerLogger = new FakeLogger<RepositoryFileWalker>();
                SecretScanner scanner = new SecretScanner(new RepositoryFileWalker(walkerLogger), AllSecretRules.Create(), _logger);

                IReadOnlyList<RawFinding> findings;
                using (FileStream holder = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    holder.Lock(10000, 1000);
                    findings = await scanner.ScanAsync(_folder.FullPath, CancellationToken.None);
                }

                FakeLogRecord warning = walkerLogger.Collector.GetSnapshot().Single(record => record.Level == LogLevel.Warning);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(findings.Select(finding => finding.FilePath), Is.EqualTo(new string[] { "publish.ps1" }));
                    Assert.That(warning.Message, Does.Contain("big.txt"));
                    Assert.That(_logger.LatestRecord.GetStructuredStateValue("UnreadableCount"), Is.EqualTo("1"));
                }
            }
            else
            {
                Assert.Ignore("Only Windows makes other readers respect a lock on part of a file");
            }
        }

        [Test]
        public async Task ScanAsync_WhenCancelled_Throws()
        {
            _folder.WriteText("a.txt", "x");
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();

                await Assert.ThatAsync(() => CreateScanner().ScanAsync(_folder.FullPath, cancellation.Token),
                    Throws.InstanceOf<OperationCanceledException>());
            }
        }

        private SecretScanner CreateScanner()
        {
            return new SecretScanner(Walker(), AllSecretRules.Create(), _logger);
        }

        private static RepositoryFileWalker Walker()
        {
            return new RepositoryFileWalker(NullLogger<RepositoryFileWalker>.Instance);
        }

        private Task<IReadOnlyList<RawFinding>> ScanAsync()
        {
            return CreateScanner().ScanAsync(_folder.FullPath, CancellationToken.None);
        }

        private static List<(string RuleId, int? LineNumber, string? SecretHash)> FindingsIn(IReadOnlyList<RawFinding> findings, string folder)
        {
            return findings
                .Where(finding => finding.FilePath.StartsWith(folder, StringComparison.Ordinal))
                .Select(finding => (finding.RuleId, finding.LineNumber, finding.SecretHash))
                .ToList();
        }

        // A rule whose pattern backtracks exponentially on a run of a's followed by something else.
        // Its own 10 ms timeout keeps the test fast; the real rules use one second.
        private sealed class SlowRule : ISecretRule
        {
            public string RuleId => "SEC000";

            public Severity Severity => Severity.Low;

            public string Message => "Slow rule for the timeout test";

            public Regex Pattern { get; } = new Regex(@"^(?<secret>(a|aa)+)$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(10));
        }
    }
}
