namespace RepoScanner.Core.Enums
{
    // How one scanner did within a scan run. When a scanner failed, its open findings are not
    // auto-resolved, because "not found" would only mean "not looked for" (task #25).
    public enum ScannerStatus
    {
        Succeeded,
        Failed
    }
}
