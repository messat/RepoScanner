namespace RepoScanner.Core.Enums
{
    // The three scanners in the one scanner image (ADR-009)
    public enum ScannerType
    {
        // Hard-coded secrets, rules SEC001 onwards
        Secret,

        // Vulnerable NuGet packages, rule PKG001
        Package,

        // Insecure code patterns, rules CP001 onwards (ADR-018)
        CodePattern
    }
}
