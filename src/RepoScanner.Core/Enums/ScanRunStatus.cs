namespace RepoScanner.Core.Enums
{
    // Where a scan run has got to. Stored as text, so the SQL shows 'Running', not 0.
    public enum ScanRunStatus
    {
        // The scan has started and not finished; task #33 skips a repository with a Running scan
        Running,

        // Every scanner succeeded
        Completed,

        // At least one scanner failed and at least one succeeded (per-scanner failure isolation, Issue 5)
        PartiallyCompleted,

        // The scan could not finish, for example the clone failed (ADR-013)
        Failed
    }
}
