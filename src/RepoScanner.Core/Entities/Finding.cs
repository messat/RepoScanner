using System;
using System.Collections.Generic;
using RepoScanner.Core.Enums;

namespace RepoScanner.Core.Entities
{
    // One problem in one repository, kept across scans: each scan updates the same row rather than
    // adding a new one. One class for all three scanners; columns that only one scanner uses are
    // nullable (ADR-010).
    public sealed class Finding
    {
        // Primary key (mapped in Data/Configurations/FindingConfiguration.cs)
        public int Id { get; set; }

        // Foreign key to Repository (required)
        public int RepositoryId { get; set; }

        // Navigation for RepositoryId: EF Core sets it when the finding is loaded with its repository
        public Repository Repository { get; set; } = null!;

        // SHA-256 in hex, unique within the repository. Excludes the line number, so a finding that
        // moves down the file is still the same finding (task #27).
        public string Fingerprint { get; set; } = string.Empty;

        public ScannerType ScannerType { get; set; }

        // SEC001, PKG001, CP001 and so on
        public string RuleId { get; set; } = string.Empty;

        public Severity Severity { get; set; }

        // Relative to the repository root
        public string FilePath { get; set; } = string.Empty;

        // For display only; not part of the fingerprint
        public int? LineNumber { get; set; }

        public string Message { get; set; } = string.Empty;

        // Secret findings only. The raw secret value is never stored anywhere.
        public string? MaskedPreview { get; set; }

        // Secret findings only: SHA-256 of the secret in hex (task #18)
        public string? SecretHash { get; set; }

        // Package findings only
        public string? PackageName { get; set; }

        public string? PackageVersion { get; set; }

        public string? AdvisoryUrl { get; set; }

        // A problem the scanner has just found is open
        public FindingStatus Status { get; set; } = FindingStatus.Open;

        public DateTime FirstSeenUtc { get; set; }

        public DateTime LastSeenUtc { get; set; }

        public DateTime? ResolvedUtc { get; set; }

        // ADR-019: empty on a suppressed finding means suppressed permanently
        public DateTime? SuppressedUntilUtc { get; set; }

        public string? SuppressedBy { get; set; }

        // ADR-019: mandatory when suppressed; the database rejects a suppression without one
        public string? SuppressionReason { get; set; }

        // This finding's history, matched on FindingEvent.FindingId
        public ICollection<FindingEvent> Events { get; set; } = new List<FindingEvent>();
    }
}
