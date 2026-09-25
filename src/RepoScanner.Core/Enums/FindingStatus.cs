namespace RepoScanner.Core.Enums
{
    // The state of a finding, kept on the Finding row across scans (ADR-019)
    public enum FindingStatus
    {
        // Found and not dealt with
        Open,

        // No longer found by a scan, or resolved by a developer in the dashboard
        Resolved,

        // Dismissed in the dashboard with a reason, until SuppressedUntilUtc or permanently
        Suppressed
    }
}
