using System;
using System.Collections.Generic;
using System.Linq;
using RepoScanner.Core.Enums;

namespace RepoScanner.Scanner.Packages
{
    // nuget.org's deprecation reasons onto Severity (Issue 5 design, decision 26): CriticalBugs can mean security
    // bugs, so High; Legacy (no longer maintained) and Other are Low. Reasons are compared ignoring case.
    public static class DeprecationSeverity
    {
        private static readonly string[] KnownReasons = { "CriticalBugs", "Legacy", "Other" };

        // The highest of the reasons. Any reason nuget.org does not document is Low: the package scanner logs a
        // warning when IsKnown is false (#75).
        public static Severity ToSeverity(IReadOnlyList<string> reasons)
        {
            return reasons.Any(reason => string.Equals(reason, "CriticalBugs", StringComparison.OrdinalIgnoreCase))
                ? Severity.High
                : Severity.Low;
        }

        public static bool IsKnown(string reason)
        {
            return KnownReasons.Contains(reason, StringComparer.OrdinalIgnoreCase);
        }
    }
}
