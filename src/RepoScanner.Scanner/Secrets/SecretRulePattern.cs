using System;
using System.Text.RegularExpressions;

namespace RepoScanner.Scanner.Secrets
{
    // Builds every rule's regular expression the same way. The timeout stops a file written to make a
    // pattern backtrack for ever from hanging the scan; SecretScanner catches it and moves on. Built
    // with new Regex rather than [GeneratedRegex]: ADR-021 prefers code that is visible in the source
    // over code a compiler feature adds.
    public static class SecretRulePattern
    {
        public static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

        public static Regex Create(string pattern, RegexOptions options = RegexOptions.None)
        {
            // CultureInvariant: matching that ignores case must not depend on the machine's culture
            // (in Turkish, "I" and "i" are different letters)
            return new Regex(pattern, RegexOptions.CultureInvariant | options, MatchTimeout);
        }
    }
}
