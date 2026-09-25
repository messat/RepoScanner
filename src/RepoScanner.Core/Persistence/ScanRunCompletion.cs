using System;
using System.Collections.Generic;
using RepoScanner.Core.Entities;
using RepoScanner.Core.Enums;

namespace RepoScanner.Core.Persistence
{
    // How a scan run ended, saved in one go by CompleteRunAsync
    public sealed record ScanRunCompletion(
        ScanRunStatus Status,
        // Empty when the clone failed before HEAD was known
        string? CommitSha,
        DateTime CompletedUtc,
        int NewCount,
        int ReopenedCount,
        int ResolvedCount,
        // Set when the run failed or partly failed (ADR-013)
        string? ErrorMessage,
        // One per scanner that ran; each must have its Status set, because an unset one reads as Succeeded
        IReadOnlyList<ScannerResult> ScannerResults);
}
