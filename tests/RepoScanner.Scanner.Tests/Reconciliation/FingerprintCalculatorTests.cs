using System;
using System.Collections.Generic;
using NUnit.Framework;
using RepoScanner.Core.Enums;
using RepoScanner.Core.Scanning;
using RepoScanner.Scanner.Reconciliation;

namespace RepoScanner.Scanner.Tests.Reconciliation
{
    // The expected fingerprints are the SHA-256 of the identity text in each test's comment, worked out
    // outside this code, so a wrong part, order or separator fails the test
    [TestFixture]
    public class FingerprintCalculatorTests
    {
        // SHA-256 of "abc" (FIPS 180-2), standing in for the hash a secret or snippet would have
        private const string HashOfAbc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

        // SHA-256 of "new HttpClient()", standing in for a code snippet's hash
        private const string HashOfSnippet = "c147280ee7290dca72ef49d317445695b5e95a73d0928e06d7d7d3def22efa86";

        // "SEC003|src/Api/appsettings.json|" + HashOfAbc: the formula Issue 3's interim code used, so
        // findings already stored still match (design decision 6)
        [Test]
        public void Calculate_ForASecret_HashesRuleFileAndSecretHash()
        {
            Assert.That(FingerprintCalculator.Calculate(SecretFinding()), Is.EqualTo("e1575900020f5028f7544bdfe5eb61e9b78452ace03801298dbf6475e5907a4d"));
        }

        // "PKG001|src/Api/Api.csproj|newtonsoft.json|12.0.1|https://github.com/advisories/GHSA-5crp-9r3c-p9vr",
        // with the package name in lower case
        [Test]
        public void Calculate_ForAPackage_HashesRuleFilePackageVersionAndAdvisory()
        {
            Assert.That(FingerprintCalculator.Calculate(PackageFinding()), Is.EqualTo("dcc9dcb4f2ea1b5cd2dd9e0321c4b912e30b020ad1bbef7a381355ef21072803"));
        }

        // "CP001|src/Services/LegacyHttpService.cs|" + HashOfSnippet
        [Test]
        public void Calculate_ForACodePattern_HashesRuleFileAndSnippetHash()
        {
            Assert.That(FingerprintCalculator.Calculate(CodePatternFinding()), Is.EqualTo("44249f7dd7f5319e184ad3b59403da8427483cafc75ca1d76c41837b92815f31"));
        }

        // A finding that moves down the file, for example when a using directive is added above it, is
        // still the same finding, so its suppression and history stay attached (ADR-024)
        [TestCaseSource(nameof(EachScannerType))]
        public void Calculate_WhenOnlyTheLineNumberChanges_IsTheSame(RawFinding finding)
        {
            RawFinding moved = finding with { LineNumber = 30 };

            Assert.That(FingerprintCalculator.Calculate(moved), Is.EqualTo(FingerprintCalculator.Calculate(finding)));
        }

        // The message, severity and preview describe the finding; they do not identify it (design decision 9)
        [TestCaseSource(nameof(DisplayOnlyChanges))]
        public void Calculate_WhenOnlyADisplayPartChanges_IsTheSame(RawFinding original, RawFinding changed)
        {
            Assert.That(FingerprintCalculator.Calculate(changed), Is.EqualTo(FingerprintCalculator.Calculate(original)));
        }

        [TestCaseSource(nameof(IdentityChanges))]
        public void Calculate_WhenAPartOfTheIdentityChanges_IsDifferent(RawFinding original, RawFinding changed)
        {
            Assert.That(FingerprintCalculator.Calculate(changed), Is.Not.EqualTo(FingerprintCalculator.Calculate(original)));
        }

        // NuGet package IDs ignore case, so Newtonsoft.Json and NEWTONSOFT.JSON are one package
        [Test]
        public void Calculate_ForAPackage_IgnoresTheCaseOfThePackageName()
        {
            RawFinding upperCase = PackageFinding() with { PackageName = "NEWTONSOFT.JSON" };

            Assert.That(FingerprintCalculator.Calculate(upperCase), Is.EqualTo(FingerprintCalculator.Calculate(PackageFinding())));
        }

        // A finding without a part of its identity comes from a scanner bug, and saving it could match it
        // with a different finding later
        [TestCaseSource(nameof(MissingParts))]
        public void Calculate_WithoutARequiredPart_ThrowsNamingIt(RawFinding finding, string part)
        {
            ArgumentException? exception = Assert.Throws<ArgumentException>(() => FingerprintCalculator.Calculate(finding));

            Assert.That(exception?.Message, Does.Contain(part));
        }

        [Test]
        public void Calculate_ForAnUnknownScannerType_Throws()
        {
            RawFinding finding = SecretFinding() with { ScannerType = (ScannerType)99 };

            Assert.Throws<ArgumentOutOfRangeException>(() => FingerprintCalculator.Calculate(finding));
        }

        private static IEnumerable<TestCaseData> EachScannerType()
        {
            yield return new TestCaseData(SecretFinding()).SetArgDisplayNames("Secret");
            yield return new TestCaseData(PackageFinding()).SetArgDisplayNames("Package");
            yield return new TestCaseData(CodePatternFinding()).SetArgDisplayNames("CodePattern");
        }

        private static IEnumerable<TestCaseData> DisplayOnlyChanges()
        {
            yield return Change("Secret Message", SecretFinding(), SecretFinding() with { Message = "Another message" });
            yield return Change("Secret Severity", SecretFinding(), SecretFinding() with { Severity = Severity.Critical });
            yield return Change("Secret MaskedPreview", SecretFinding(), SecretFinding() with { MaskedPreview = "****" });
            yield return Change("Package Message", PackageFinding(), PackageFinding() with { Message = "Another message" });
            yield return Change("CodePattern Severity", CodePatternFinding(), CodePatternFinding() with { Severity = Severity.High });
        }

        private static IEnumerable<TestCaseData> IdentityChanges()
        {
            yield return Change("Secret RuleId", SecretFinding(), SecretFinding() with { RuleId = "SEC007" });
            yield return Change("Secret FilePath", SecretFinding(), SecretFinding() with { FilePath = "src/Api/appsettings.Development.json" });
            yield return Change("Secret SecretHash", SecretFinding(), SecretFinding() with { SecretHash = HashOfSnippet });
            yield return Change("Package RuleId", PackageFinding(), PackageFinding() with { RuleId = "PKG002" });
            yield return Change("Package FilePath", PackageFinding(), PackageFinding() with { FilePath = "src/Web/Web.csproj" });
            yield return Change("Package PackageName", PackageFinding(), PackageFinding() with { PackageName = "Microsoft.Data.SqlClient" });
            yield return Change("Package PackageVersion", PackageFinding(), PackageFinding() with { PackageVersion = "12.0.2" });
            yield return Change("Package AdvisoryUrl", PackageFinding(), PackageFinding() with { AdvisoryUrl = "https://github.com/advisories/GHSA-cmhx-cq75-c4mj" });
            yield return Change("CodePattern RuleId", CodePatternFinding(), CodePatternFinding() with { RuleId = "CP002" });
            yield return Change("CodePattern FilePath", CodePatternFinding(), CodePatternFinding() with { FilePath = "src/Services/GoodHttpService.cs" });
            yield return Change("CodePattern SnippetHash", CodePatternFinding(), CodePatternFinding() with { SnippetHash = HashOfAbc });
        }

        private static IEnumerable<TestCaseData> MissingParts()
        {
            yield return new TestCaseData(SecretFinding() with { SecretHash = null }, "SecretHash").SetArgDisplayNames("Secret without SecretHash");
            yield return new TestCaseData(SecretFinding() with { SecretHash = "" }, "SecretHash").SetArgDisplayNames("Secret with an empty SecretHash");
            yield return new TestCaseData(PackageFinding() with { PackageName = null }, "PackageName").SetArgDisplayNames("Package without PackageName");
            yield return new TestCaseData(PackageFinding() with { PackageVersion = null }, "PackageVersion").SetArgDisplayNames("Package without PackageVersion");
            yield return new TestCaseData(PackageFinding() with { AdvisoryUrl = null }, "AdvisoryUrl").SetArgDisplayNames("Package without AdvisoryUrl");
            yield return new TestCaseData(CodePatternFinding() with { SnippetHash = null }, "SnippetHash").SetArgDisplayNames("CodePattern without SnippetHash");
        }

        private static TestCaseData Change(string name, RawFinding original, RawFinding changed)
        {
            return new TestCaseData(original, changed).SetArgDisplayNames(name);
        }

        private static RawFinding SecretFinding()
        {
            return new RawFinding
            {
                ScannerType = ScannerType.Secret,
                RuleId = "SEC003",
                Severity = Severity.High,
                FilePath = "src/Api/appsettings.json",
                LineNumber = 3,
                Message = "Password in a connection string",
                MaskedPreview = "Pa****",
                SecretHash = HashOfAbc
            };
        }

        private static RawFinding PackageFinding()
        {
            return new RawFinding
            {
                ScannerType = ScannerType.Package,
                RuleId = "PKG001",
                Severity = Severity.High,
                FilePath = "src/Api/Api.csproj",
                LineNumber = 3,
                Message = "Vulnerable package",
                PackageName = "Newtonsoft.Json",
                PackageVersion = "12.0.1",
                AdvisoryUrl = "https://github.com/advisories/GHSA-5crp-9r3c-p9vr"
            };
        }

        private static RawFinding CodePatternFinding()
        {
            return new RawFinding
            {
                ScannerType = ScannerType.CodePattern,
                RuleId = "CP001",
                Severity = Severity.Low,
                FilePath = "src/Services/LegacyHttpService.cs",
                LineNumber = 3,
                Message = "new HttpClient() per call",
                SnippetHash = HashOfSnippet
            };
        }
    }
}
