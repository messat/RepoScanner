using System.Collections.Generic;
using System.IO;

namespace RepoScanner.Scanner.Tests.CodePatterns
{
    // One code-pattern finding the scanner must report in the fixture, and where. Each is marked in the file with
    // a comment naming its rule.
    internal sealed record FixturePattern(string RuleId, string FilePath, int LineNumber);

    // The labels for tests/fixtures/patterns-repo (#54): every code-pattern finding in it. Next to each is a case
    // the rule must not report, and Quiet/ holds every pattern in comments, strings and a .txt file only, so it
    // gives none. The folder has its own .reposcanner-exclude, so the nightly scan of this repository does not
    // report them.
    internal static class FixturePatterns
    {
        public static string FolderPath { get; } = Path.Combine(RepositoryRoot.FullPath, "tests", "fixtures", "patterns-repo");

        // In the order the scanner returns them: by file (ordinal), line, then rule
        public static IReadOnlyList<FixturePattern> All { get; } = new FixturePattern[]
        {
            // MD5.Create(), SHA1.HashData(bytes) and new TripleDESCryptoServiceProvider(); not SHA256.Create()
            new FixturePattern("CP004", "Crypto/Checksums.cs", 10),
            new FixturePattern("CP004", "Crypto/Checksums.cs", 18),
            new FixturePattern("CP004", "Crypto/Checksums.cs", 23),

            // +, $"{hole}", string.Format, new SqlCommand(...) and CommandText in an initialiser; not FromSql, a
            // join of literals, a parameter placeholder or a variable
            new FixturePattern("CP001", "Data/UserQueries.cs", 15),
            new FixturePattern("CP001", "Data/UserQueries.cs", 20),
            new FixturePattern("CP001", "Data/UserQueries.cs", 25),
            new FixturePattern("CP001", "Data/UserQueries.cs", 30),
            new FixturePattern("CP001", "Data/UserQueries.cs", 36),

            // Two empty catches in different methods, so two findings; not one with a comment inside, or throw;
            new FixturePattern("CP006", "Errors/Catches.cs", 16),
            new FixturePattern("CP006", "Errors/Catches.cs", 27),

            // => true, DangerousAcceptAnyServerCertificateValidator and += delegate { return true; }; not a check
            new FixturePattern("CP003", "Http/InsecureClient.cs", 13),
            new FixturePattern("CP003", "Http/InsecureClient.cs", 21),
            new FixturePattern("CP003", "Http/InsecureClient.cs", 28),

            // The four formatters, then TypeNameHandling in settings and in an attribute; not TypeNameHandling.None
            new FixturePattern("CP002", "Serialization/LegacyCache.cs", 14),
            new FixturePattern("CP002", "Serialization/LegacyCache.cs", 20),
            new FixturePattern("CP002", "Serialization/LegacyCache.cs", 25),
            new FixturePattern("CP002", "Serialization/LegacyCache.cs", 30),
            new FixturePattern("CP002", "Serialization/LegacyCache.cs", 37),
            new FixturePattern("CP002", "Serialization/LegacyCache.cs", 52),

            // AllowAnyOrigin() and SetIsOriginAllowed(origin => true); not WithOrigins
            new FixturePattern("CP005", "Web/CorsSetup.cs", 13),
            new FixturePattern("CP005", "Web/CorsSetup.cs", 14)
        };
    }
}
