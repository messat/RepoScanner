namespace RepoScanner.Core.Enums
{
    // Which lane started the scan (ADR-011): the nightly schedule or "Scan now" in the dashboard
    public enum ScanTrigger
    {
        Nightly,
        OnDemand
    }
}
