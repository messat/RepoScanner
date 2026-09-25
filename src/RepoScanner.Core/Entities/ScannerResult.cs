using RepoScanner.Core.Enums;

namespace RepoScanner.Core.Entities
{
    // How one scanner did within one scan run: one row per scanner that ran
    public sealed class ScannerResult
    {
        // Primary key (mapped in Data/Configurations/ScannerResultConfiguration.cs)
        public int Id { get; set; }

        // Foreign key to ScanRun (required)
        public int ScanRunId { get; set; }

        // Navigation for ScanRunId: EF Core sets it when the result is loaded with its run
        public ScanRun ScanRun { get; set; } = null!;

        public ScannerType ScannerType { get; set; }

        public ScannerStatus Status { get; set; }

        public int FindingCount { get; set; }

        public int DurationMs { get; set; }

        // Set when the scanner failed
        public string? ErrorMessage { get; set; }
    }
}
