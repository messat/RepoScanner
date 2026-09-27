using RepoScanner.Core.Persistence;

namespace RepoScanner.Scanner.Reconciliation
{
    // What one scan changes about a repository's findings, and how many of each kind of change there
    // were. The first three counts go on the scan run; SuppressionExpiredCount is only logged, as
    // ScanRuns has no column for it.
    public sealed record ReconciliationResult(
        FindingChanges Changes,
        int NewCount,
        int ReopenedCount,
        int ResolvedCount,
        int SuppressionExpiredCount);
}
