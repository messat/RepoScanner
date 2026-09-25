using System;
using System.Collections.Generic;
using RepoScanner.Core.Enums;

namespace RepoScanner.Core.Entities
{
    // One scan of one repository. A new row is added for every scan and kept: scan history is the
    // evidence for raising maxExecutions (ADR-012) and keeps a failed scan visible (ADR-013).
    public sealed class ScanRun
    {
        // Primary key (mapped in Data/Configurations/ScanRunConfiguration.cs)
        public int Id { get; set; }

        // Foreign key to Repository (required)
        public int RepositoryId { get; set; }

        // Navigation for RepositoryId: EF Core sets it when the run is loaded with its repository
        public Repository Repository { get; set; } = null!;

        // Not called Trigger: TRIGGER is a reserved word in SQL Server
        public ScanTrigger TriggerType { get; set; }

        // A run is created when the scan starts
        public ScanRunStatus Status { get; set; } = ScanRunStatus.Running;

        // Empty when the clone failed before HEAD was known
        public string? CommitSha { get; set; }

        // From the queue message, so every log line of the run can be tied back to it (task #62)
        public Guid CorrelationId { get; set; }

        public DateTime StartedUtc { get; set; }

        // Empty while the run is Running
        public DateTime? CompletedUtc { get; set; }

        public int NewCount { get; set; }

        public int ReopenedCount { get; set; }

        public int ResolvedCount { get; set; }

        // Set when the run failed (ADR-013), so Log Analytics is not the only record
        public string? ErrorMessage { get; set; }

        // One result per scanner that ran, matched on ScannerResult.ScanRunId
        public ICollection<ScannerResult> ScannerResults { get; set; } = new List<ScannerResult>();
    }
}
