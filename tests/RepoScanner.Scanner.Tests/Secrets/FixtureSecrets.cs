using System.Collections.Generic;
using System.IO;

namespace RepoScanner.Scanner.Tests.Secrets
{
    // The labels for tests/fixtures/secrets-repo (#21): every fake secret in it and where the scanner
    // must report it. This folder has its own .reposcanner-exclude, so the nightly scan of this
    // repository does not report these values either.
    internal static class FixtureSecrets
    {
        public static string FolderPath { get; } = Path.Combine(RepositoryRoot.FullPath, "tests", "fixtures", "secrets-repo");

        // In the order the scanner returns them: by file (ordinal, so upper case before lower), then line
        public static IReadOnlyList<FixtureSecret> All { get; } = new FixtureSecret[]
        {
            new FixtureSecret("SEC007", "deploy/app.env", 2, "FAKEdevopspat000000000000000000000000000000000000000"),
            new FixtureSecret("SEC004", "deploy/publish.ps1", 2, "FAKE-feed-pa55"),
            new FixtureSecret("SEC006", "deploy/publish.ps1", 3, "oy2fakenugetapikeyforreposcannertests000000000"),
            new FixtureSecret("SEC001", "deploy/signing-key.pem", 1,
                "MIIEFAKEprivateKeyForRepoScannerTestsAAAAAAAAAAAAAAAAAAAAAAAAAAA" + "FAKEsecondLineOfTheKeyBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB"),
            new FixtureSecret("SEC002", "src/Api/StorageSettings.cs", 7, "FAKEstorageAccountKeyForRepoScannerTests0000000000000000000000000000000000000000000000=="),
            new FixtureSecret("SEC005", "src/Api/StorageSettings.cs", 9, "FAK8Q~FAKE.client_secret~for-RepoScanner"),
            new FixtureSecret("SEC007", "src/Api/Web.config", 5, "FAKE-client-secret-value-42"),
            new FixtureSecret("SEC003", "src/Api/appsettings.json", 3, "FAKE-Orders-Pa55word"),
            new FixtureSecret("SEC007", "src/Api/appsettings.json", 6, "FAKE0payments0api0key0123456789")
        };
    }
}
