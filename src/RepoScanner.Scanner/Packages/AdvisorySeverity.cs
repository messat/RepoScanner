using RepoScanner.Core.Enums;

namespace RepoScanner.Scanner.Packages
{
    // nuget.org's severity numbers onto Severity: 0 low, 1 medium, 2 high and 3 critical, as nuget.org's
    // vulnerability info resource documents them (Issue 5 design, decision 15)
    public static class AdvisorySeverity
    {
        // Any other value is High: over-rating is safer than dropping a vulnerability. The package scanner logs
        // a warning when IsKnown is false (#46).
        public static Severity ToSeverity(int value)
        {
            return value switch
            {
                0 => Severity.Low,
                1 => Severity.Medium,
                2 => Severity.High,
                3 => Severity.Critical,
                _ => Severity.High
            };
        }

        public static bool IsKnown(int value)
        {
            return value >= 0 && value <= 3;
        }
    }
}
