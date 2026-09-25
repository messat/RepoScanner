using System.Text.RegularExpressions;
using RepoScanner.Core.Enums;

namespace RepoScanner.Scanner.Secrets.Rules
{
    // A nuget.org API key. It lets anyone publish packages under Blacklight's name, so it is High
    // (Issue 3 design, Secret rules).
    public sealed class Sec006NuGetApiKeyRule : ISecretRule
    {
        public string RuleId => "SEC006";

        public Severity Severity => Severity.High;

        public string Message => "NuGet API key";

        // oy2 and exactly 43 lower-case letters and digits, as a whole token. Case-sensitive: the
        // format is lower case, so upper-case letters mean something else.
        public Regex Pattern { get; } = SecretRulePattern.Create(
            @"(?<![A-Za-z0-9])(?<secret>oy2[a-z0-9]{43})(?![A-Za-z0-9])");
    }
}
