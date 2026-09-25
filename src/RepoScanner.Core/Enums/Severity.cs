namespace RepoScanner.Core.Enums
{
    // How serious a finding is. Package findings map the advisory's severity onto these (task #45).
    public enum Severity
    {
        Low,
        Medium,
        High,
        Critical
    }
}
