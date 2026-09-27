using RepoScanner.Core.Enums;

namespace RepoScanner.Core.Scanning
{
    // What a scanner reports about one problem, before it is matched against the stored findings. It
    // holds the Finding columns a scanner can know, plus SnippetHash, which only feeds the fingerprint
    // (Issue 4 design). There is deliberately no property for a secret's raw value, so one can never be
    // passed on or saved by mistake (Issue 3 design).
    public sealed record RawFinding
    {
        public required ScannerType ScannerType { get; init; }

        // SEC001, PKG001, CP001 and so on
        public required string RuleId { get; init; }

        public required Severity Severity { get; init; }

        // Relative to the scanned folder, with / between folders
        public required string FilePath { get; init; }

        // Counted from 1. For display only: not part of the fingerprint (#27)
        public int? LineNumber { get; init; }

        public required string Message { get; init; }

        // Secret findings only
        public string? MaskedPreview { get; init; }

        // Secret findings only: SHA-256 of the secret in hex (ADR-023)
        public string? SecretHash { get; init; }

        // Package findings only (Issue 7)
        public string? PackageName { get; init; }

        public string? PackageVersion { get; init; }

        public string? AdvisoryUrl { get; init; }

        // Code-pattern findings only (Issue 8): SHA-256 of the matched code, which identifies the finding
        // without its line number (ADR-024). Not stored: the fingerprint carries it.
        public string? SnippetHash { get; init; }
    }
}
