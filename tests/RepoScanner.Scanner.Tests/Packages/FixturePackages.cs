using System.Collections.Generic;
using System.IO;
using RepoScanner.Core.Enums;

namespace RepoScanner.Scanner.Tests.Packages
{
    // One package finding the scanner must report in the fixture, where, and why
    internal sealed record FixturePackage(
        string RuleId, string FilePath, int LineNumber, string PackageName, string PackageVersion, string AdvisoryUrl, Severity Severity, string Message);

    // The labels for tests/fixtures/packages-repo (#43, #46, #75): every package finding in it, from nuget.org's
    // data on 2026-09-28 (Packages/NuGetData). The folder has its own .reposcanner-exclude, so the nightly scan of
    // this repository does not report them.
    internal static class FixturePackages
    {
        public static string FolderPath { get; } = Path.Combine(RepositoryRoot.FullPath, "tests", "fixtures", "packages-repo");

        // In the order the scanner returns them: by file (ordinal), line, package, rule, then link
        public static IReadOnlyList<FixturePackage> All { get; } = new FixturePackage[]
        {
            // Set once in the props file for Api and Worker, so reported there only (design decision 5). It is
            // vulnerable twice over, and deprecated too.
            new FixturePackage("PKG001", "central/Directory.Packages.props", 6, "Microsoft.Identity.Client", "4.59.0",
                "https://github.com/advisories/GHSA-m5vv-6r4h-3vj9", Severity.Medium, "Microsoft.Identity.Client 4.59.0 has a known vulnerability"),
            new FixturePackage("PKG001", "central/Directory.Packages.props", 6, "Microsoft.Identity.Client", "4.59.0",
                "https://github.com/advisories/GHSA-x674-v45j-fwxw", Severity.Low, "Microsoft.Identity.Client 4.59.0 has a known vulnerability"),
            new FixturePackage("PKG002", "central/Directory.Packages.props", 6, "Microsoft.Identity.Client", "4.59.0",
                "https://www.nuget.org/packages/microsoft.identity.client/4.59.0", Severity.Low, "Microsoft.Identity.Client 4.59.0 is deprecated (legacy)"),

            // VersionOverride="4.5.0" in the project, not the props file's safe 8.0.0
            new FixturePackage("PKG001", "central/Worker/Worker.csproj", 9, "System.Text.Encodings.Web", "4.5.0",
                "https://github.com/advisories/GHSA-ghhp-997w-qr28", Severity.Critical, "System.Text.Encodings.Web 4.5.0 has a known vulnerability"),

            // 12.0.* is 12.0.3, the newest 12.0.x release on nuget.org, which is below the fixed 13.0.1
            new FixturePackage("PKG001", "plain/Plain.csproj", 9, "Newtonsoft.Json", "12.0.3",
                "https://github.com/advisories/GHSA-5crp-9r3c-p9vr", Severity.High, "Newtonsoft.Json 12.0.3 (from 12.0.*) has a known vulnerability"),

            // Two advisories match one version: two findings
            new FixturePackage("PKG001", "plain/Plain.csproj", 11, "System.Text.Json", "8.0.3",
                "https://github.com/advisories/GHSA-8g4q-xg66-9fp4", Severity.High, "System.Text.Json 8.0.3 has a known vulnerability"),
            new FixturePackage("PKG001", "plain/Plain.csproj", 11, "System.Text.Json", "8.0.3",
                "https://github.com/advisories/GHSA-hh2w-p6rv-4g7w", Severity.High, "System.Text.Json 8.0.3 has a known vulnerability"),

            // Deprecated with an alternative named, and no advisories (#75)
            new FixturePackage("PKG002", "plain/Plain.csproj", 12, "Microsoft.Azure.Storage.Blob", "11.2.3",
                "https://www.nuget.org/packages/microsoft.azure.storage.blob/11.2.3", Severity.Low,
                "Microsoft.Azure.Storage.Blob 11.2.3 is deprecated (legacy); use Azure.Storage.Blobs instead")
        };
    }
}
