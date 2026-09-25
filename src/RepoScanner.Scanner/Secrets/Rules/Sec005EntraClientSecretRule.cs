using System.Text.RegularExpressions;
using RepoScanner.Core.Enums;

namespace RepoScanner.Scanner.Secrets.Rules
{
    // An Entra ID (Azure AD) application's client secret, recognised by its format, which contains
    // "Q~". It lets anyone sign in as the application, so it is High (Issue 3 design, Secret rules).
    public sealed class Sec005EntraClientSecretRule : ISecretRule
    {
        public string RuleId => "SEC005";

        public Severity Severity => Severity.High;

        public string Message => "Entra ID client secret";

        // 3 characters, a digit, Q~, then 31 to 34 characters. The lookarounds make it a whole token,
        // not part of a longer one. Case-sensitive: the Q is always upper case.
        public Regex Pattern { get; } = SecretRulePattern.Create(
            @"(?<![A-Za-z0-9_~.-])(?<secret>[A-Za-z0-9_~.]{3}[0-9]Q~[A-Za-z0-9_~.-]{31,34})(?![A-Za-z0-9_~.-])");
    }
}
