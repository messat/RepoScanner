using System;
using RepoScanner.Core.Enums;

namespace RepoScanner.Core.Entities
{
    // One change in a finding's life, such as Detected or Suppressed. Only ever added, never
    // changed or deleted, so a finding's events are its audit trail (ADR-019).
    public sealed class FindingEvent
    {
        // Primary key (mapped in Data/Configurations/FindingEventConfiguration.cs)
        public int Id { get; set; }

        // Foreign key to Finding (required)
        public int FindingId { get; set; }

        // Navigation for FindingId: EF Core sets it when the event is loaded with its finding
        public Finding Finding { get; set; } = null!;

        // Foreign key to ScanRun (optional): the scan that caused the event, empty for actions
        // taken in the dashboard
        public int? ScanRunId { get; set; }

        // Navigation for ScanRunId; empty when ScanRunId is empty
        public ScanRun? ScanRun { get; set; }

        public FindingEventType EventType { get; set; }

        public DateTime OccurredUtc { get; set; }

        // "scanner", or the developer's sign-in name for dashboard actions
        public string Actor { get; set; } = string.Empty;

        // For example the suppression reason
        public string? Detail { get; set; }
    }
}
