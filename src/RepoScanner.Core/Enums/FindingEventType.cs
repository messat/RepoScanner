namespace RepoScanner.Core.Enums
{
    // A change in a finding's life. Events are only ever added, never deleted, so together they
    // are the finding's audit trail (ADR-019).
    public enum FindingEventType
    {
        // The scanner found it for the first time
        Detected,

        // The scanner no longer found it, or a developer resolved it
        Resolved,

        // The scanner found a resolved finding again
        Reopened,

        // A developer suppressed it in the dashboard
        Suppressed,

        // Its suppression reached SuppressedUntilUtc and it returned to Open
        SuppressionExpired
    }
}
