using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using NUnit.Framework;
using RepoScanner.Core.Enums;
using RepoScanner.Core.Scanning;
using RepoScanner.Scanner.CodePatterns;
using RepoScanner.Scanner.Files;
using RepoScanner.Scanner.Packages;
using RepoScanner.Scanner.Reconciliation;
using RepoScanner.Scanner.Secrets;

namespace RepoScanner.Scanner.Tests.Packages
{
    // The package scanner (#46): the reader (#43), nuget.org through FakeNuGet (#44) and the resolver (#45) together
    [TestFixture]
    public class PackageScannerTests
    {
        private TempFolder _folder = null!;
        private FakeNuGet _nuGet = null!;
        private ServiceProvider _services = null!;
        private FakeLogger<PackageScanner> _logger = null!;
        private PackageScanner _scanner = null!;

        [SetUp]
        public void SetUp()
        {
            _folder = new TempFolder();
            _nuGet = new FakeNuGet();
            _logger = new FakeLogger<PackageScanner>();

            ServiceCollection services = new ServiceCollection();
            services.AddSingleton<ILogger<NuGetVulnerabilityIndexClient>>(NullLogger<NuGetVulnerabilityIndexClient>.Instance);
            NuGetVulnerabilityIndexClient.Register(services);
            _nuGet.ReplaceNuGetOrg(services);
            _services = services.BuildServiceProvider();

            ProjectFileReader reader = new ProjectFileReader(
                new RepositoryFileWalker(NullLogger<RepositoryFileWalker>.Instance), NullLogger<ProjectFileReader>.Instance);
            _scanner = new PackageScanner(reader, _services.GetRequiredService<NuGetVulnerabilityIndexClient>(), _logger);
        }

        [TearDown]
        public void TearDown()
        {
            _services.Dispose();
            _nuGet.Dispose();
            _folder.Dispose();
            Assert.That(_nuGet.UnknownAddresses, Is.Empty, "requests that would have reached the internet");
        }

        [Test]
        public void ScannerType_IsPackage()
        {
            Assert.That(_scanner.ScannerType, Is.EqualTo(ScannerType.Package));
        }

        // Issue 5 criteria 1 and 5: known-vulnerable versions give PKG001 findings with the right severity and
        // link, and deprecated ones PKG002 findings with their reasons and any alternative
        [Test]
        public async Task ScanAsync_OnTheFixture_FindsExactlyTheLabelledPackages()
        {
            IReadOnlyList<RawFinding> findings = await _scanner.ScanAsync(FixturePackages.FolderPath, CancellationToken.None);

            Assert.That(findings.Select(finding => new FixturePackage(finding.RuleId, finding.FilePath, finding.LineNumber!.Value, finding.PackageName!,
                finding.PackageVersion!, finding.AdvisoryUrl!, finding.Severity, finding.Message)), Is.EqualTo(FixturePackages.All));
        }

        [Test]
        public async Task ScanAsync_OnTheFixture_GivesPackageFindingsTheFingerprintAccepts()
        {
            IReadOnlyList<RawFinding> findings = await _scanner.ScanAsync(FixturePackages.FolderPath, CancellationToken.None);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(findings, Is.Not.Empty);
                Assert.That(findings.Select(finding => finding.RuleId), Is.All.AnyOf("PKG001", "PKG002"));
                Assert.That(findings.Select(finding => finding.ScannerType), Is.All.EqualTo(ScannerType.Package));
                Assert.That(findings.Select(finding => finding.MaskedPreview ?? finding.SecretHash ?? finding.SnippetHash), Is.All.Null);
                Assert.That(() => findings.Select(FingerprintCalculator.Calculate).ToList(), Throws.Nothing);
            }
        }

        [Test]
        public async Task ScanAsync_OnTheFixture_LogsOneSummaryLineWithWhatItSkipped()
        {
            await _scanner.ScanAsync(FixturePackages.FolderPath, CancellationToken.None);

            FakeLogRecord summary = _logger.LatestRecord;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(summary.Level, Is.EqualTo(LogLevel.Information));
                Assert.That(summary.GetStructuredStateValue("ProjectFileCount"), Is.EqualTo("6"));
                Assert.That(summary.GetStructuredStateValue("PackageCount"), Is.EqualTo("8"));
                Assert.That(summary.GetStructuredStateValue("FindingCount"), Is.EqualTo("8"));
                Assert.That(summary.GetStructuredStateValue("InvalidFileCount"), Is.EqualTo("1"));
                Assert.That(summary.GetStructuredStateValue("PropertyVersionCount"), Is.EqualTo("1"));
                Assert.That(summary.GetStructuredStateValue("MissingVersionCount"), Is.EqualTo("0"));
                Assert.That(summary.GetStructuredStateValue("NotOnNuGetCount"), Is.EqualTo("0"));
                Assert.That(summary.GetStructuredStateValue("UnresolvedCount"), Is.EqualTo("0"));
            }
        }

        // Deprecation needs the real version even when there are no advisories (#75, decision 29, which replaced
        // #46's shortcut): Microsoft.CrmSdk.CoreAssemblies 9.0.2.* in the plug-in project is resolved to 9.0.2.60
        // and its metadata read
        [Test]
        public async Task ScanAsync_AFloatingVersionOfAPackageWithNoAdvisories_IsStillResolvedForDeprecation()
        {
            await _scanner.ScanAsync(FixturePackages.FolderPath, CancellationToken.None);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_nuGet.RequestCount(FakeNuGet.VersionListUrl("Microsoft.CrmSdk.CoreAssemblies")), Is.EqualTo(1));
                Assert.That(_nuGet.RequestCount(FakeNuGet.RegistrationIndexUrl("Microsoft.CrmSdk.CoreAssemblies")), Is.EqualTo(1));
            }
        }

        // The victim repository's Microsoft.Data.SqlClient 5.1.0: vulnerable (PKG001) and deprecated for critical
        // bugs (PKG002), so two findings on one line, which never share a fingerprint
        [Test]
        public async Task ScanAsync_AVersionThatIsVulnerableAndDeprecated_GivesBothFindings()
        {
            WriteProject("<PackageReference Include=\"Microsoft.Data.SqlClient\" Version=\"5.1.0\" />");

            IReadOnlyList<RawFinding> findings = await _scanner.ScanAsync(_folder.FullPath, CancellationToken.None);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(findings.Select(finding => (finding.RuleId, finding.Severity, finding.AdvisoryUrl)), Is.EqualTo(new[]
                {
                    ("PKG001", Severity.High, "https://github.com/advisories/GHSA-98g6-xh36-x2p7"),
                    ("PKG002", Severity.High, "https://www.nuget.org/packages/microsoft.data.sqlclient/5.1.0")
                }));
                Assert.That(findings[1].Message, Is.EqualTo("Microsoft.Data.SqlClient 5.1.0 is deprecated (critical bugs)"));
                Assert.That(findings.Select(FingerprintCalculator.Calculate).Distinct().Count(), Is.EqualTo(2));
            }
        }

        [Test]
        public async Task ScanAsync_AResolvedDeprecatedVersion_SaysWhatWasWritten()
        {
            _nuGet.Respond(FakeNuGet.VersionListUrl("Microsoft.Azure.Storage.Blob"), "{ \"versions\": [ \"11.2.2\", \"11.2.3\" ] }");
            WriteProject("<PackageReference Include=\"Microsoft.Azure.Storage.Blob\" Version=\"11.2.*\" />");

            RawFinding finding = (await _scanner.ScanAsync(_folder.FullPath, CancellationToken.None)).Single();

            Assert.That(finding.Message, Is.EqualTo("Microsoft.Azure.Storage.Blob 11.2.3 (from 11.2.*) is deprecated (legacy); use Azure.Storage.Blobs instead"));
        }

        [Test]
        public async Task ScanAsync_AnUnknownDeprecationReason_IsLowWithAWarningNamingThePackage()
        {
            _nuGet.Respond(FakeNuGet.RegistrationIndexUrl("Serilog"),
                "{ \"items\": [ { \"@id\": \"" + FakeNuGet.RegistrationsBaseUrl + "serilog/index.json#page/3.0.0/3.9.9\", \"lower\": \"3.0.0\", \"upper\": \"3.9.9\", " +
                "\"items\": [ { \"catalogEntry\": { \"id\": \"Serilog\", \"version\": \"3.1.1\", \"deprecation\": { \"reasons\": [ \"SomethingNew\" ] } } } ] } ] }");
            WriteProject("<PackageReference Include=\"Serilog\" Version=\"3.1.1\" />");

            RawFinding finding = (await _scanner.ScanAsync(_folder.FullPath, CancellationToken.None)).Single();

            FakeLogRecord warning = _logger.Collector.GetSnapshot().Single(record => record.Level == LogLevel.Warning);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(finding.RuleId, Is.EqualTo("PKG002"));
                Assert.That(finding.Severity, Is.EqualTo(Severity.Low));
                Assert.That(finding.Message, Is.EqualTo("Serilog 3.1.1 is deprecated (somethingnew)"));
                Assert.That(warning.GetStructuredStateValue("PackageId"), Is.EqualTo("Serilog"));
            }
        }

        // Issue 5 design, decision 19: nothing to check, so nuget.org is not asked
        [Test]
        public async Task ScanAsync_AFolderWithNoProjectFiles_MakesNoRequest()
        {
            _folder.WriteText("src/appsettings.json", "{ }");

            IReadOnlyList<RawFinding> findings = await _scanner.ScanAsync(_folder.FullPath, CancellationToken.None);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(findings, Is.Empty);
                Assert.That(_nuGet.Requests, Is.Empty);
            }
        }

        [Test]
        public async Task ScanAsync_ASafeVersion_GivesNoFinding()
        {
            WriteProject("<PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />");

            IReadOnlyList<RawFinding> findings = await _scanner.ScanAsync(_folder.FullPath, CancellationToken.None);

            Assert.That(findings, Is.Empty);
        }

        // nuget.org repeats a link once per affected range: a version in two of them is one finding, as serious
        // as the worse of the two (Issue 5 design, decision 14)
        [Test]
        public async Task ScanAsync_TwoEntriesWithOneLink_GiveOneFindingWithTheHigherSeverity()
        {
            _nuGet.Respond(_nuGet.UpdatePageUrl,
                "{ \"newtonsoft.json\": [ { \"url\": \"https://github.com/advisories/GHSA-5crp-9r3c-p9vr\", \"severity\": 3, \"versions\": \"[12.0.0, 12.0.2)\" } ] }");
            WriteProject("<PackageReference Include=\"Newtonsoft.Json\" Version=\"12.0.1\" />");

            RawFinding finding = (await _scanner.ScanAsync(_folder.FullPath, CancellationToken.None)).Single();

            Assert.That(finding.Severity, Is.EqualTo(Severity.Critical));
        }

        [Test]
        public async Task ScanAsync_AnUnknownSeverity_IsHighWithAWarningNamingTheAdvisory()
        {
            _nuGet.Respond(_nuGet.UpdatePageUrl,
                "{ \"serilog\": [ { \"url\": \"https://github.com/advisories/GHSA-odd0-0000-0000\", \"severity\": 7, \"versions\": \"(, 4.0.0)\" } ] }");
            WriteProject("<PackageReference Include=\"Serilog\" Version=\"3.1.1\" />");

            RawFinding finding = (await _scanner.ScanAsync(_folder.FullPath, CancellationToken.None)).Single();

            FakeLogRecord warning = _logger.Collector.GetSnapshot().Single(record => record.Level == LogLevel.Warning);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(finding.Severity, Is.EqualTo(Severity.High));
                Assert.That(warning.GetStructuredStateValue("AdvisoryUrl"), Is.EqualTo("https://github.com/advisories/GHSA-odd0-0000-0000"));
            }
        }

        // FakeNuGet has no version list for System.Text.Json, so nuget.org answers 404, as for a private package
        [Test]
        public async Task ScanAsync_AFloatingVersionNuGetOrgCannotList_IsSkippedAndCounted()
        {
            WriteProject("<PackageReference Include=\"System.Text.Json\" Version=\"8.0.*\" />");

            IReadOnlyList<RawFinding> findings = await _scanner.ScanAsync(_folder.FullPath, CancellationToken.None);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(findings, Is.Empty);
                Assert.That(_logger.LatestRecord.GetStructuredStateValue("NotOnNuGetCount"), Is.EqualTo("1"));
            }
        }

        [TestCase("99.*", Description = "no Newtonsoft.Json 99.x on nuget.org")]
        [TestCase("latest", Description = "not a version or range")]
        public async Task ScanAsync_AVersionThatDoesNotResolve_IsSkippedAndCounted(string versionText)
        {
            WriteProject("<PackageReference Include=\"Newtonsoft.Json\" Version=\"" + versionText + "\" />");

            IReadOnlyList<RawFinding> findings = await _scanner.ScanAsync(_folder.FullPath, CancellationToken.None);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(findings, Is.Empty);
                Assert.That(_logger.LatestRecord.GetStructuredStateValue("UnresolvedCount"), Is.EqualTo("1"));
            }
        }

        // The scanner fails on its own; LocalScanRunner decides what the run does (#74)
        [Test]
        public async Task ScanAsync_WhenNuGetOrgIsDown_ThrowsNuGetUnavailable()
        {
            _nuGet.Fail();
            WriteProject("<PackageReference Include=\"Newtonsoft.Json\" Version=\"12.0.1\" />");

            await Assert.ThatAsync(() => _scanner.ScanAsync(_folder.FullPath, CancellationToken.None), Throws.InstanceOf<NuGetUnavailableException>());
        }

        // Local mode runs every registered scanner, secrets first (Issue 5 design, Wiring), and code patterns
        // last (Issue 6 design, #49)
        [Test]
        public void CreateBuilder_RegistersThePackageScannerAfterTheSecretScanner()
        {
            string[] args = { "--path", FixturePackages.FolderPath, "--Scanner:Database:ConnectionString", "Server=unused;Database=unused" };

            using (IHost host = Program.CreateBuilder(args).Build())
            {
                IEnumerable<IScanner> scanners = host.Services.GetServices<IScanner>();

                Assert.That(scanners.Select(scanner => scanner.GetType()),
                    Is.EqualTo(new[] { typeof(SecretScanner), typeof(PackageScanner), typeof(CodePatternScanner) }));
            }
        }

        private void WriteProject(string reference)
        {
            _folder.WriteText("Api/Api.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
                "  <ItemGroup>\n" +
                "    " + reference + "\n" +
                "  </ItemGroup>\n" +
                "</Project>\n");
        }
    }
}
