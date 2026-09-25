using System.Text.RegularExpressions;
using RepoScanner.Core.Enums;

namespace RepoScanner.Scanner.Secrets.Rules
{
    // A user name and password written into a URL, as in https://user:password@host. ADR-016 calls
    // this "exactly the pattern this tool flags". High (Issue 3 design, Secret rules).
    public sealed class Sec004UrlCredentialsRule : ISecretRule
    {
        public string RuleId => "SEC004";

        public Severity Severity => Severity.High;

        public string Message => "Credentials in a URL";

        // scheme://user:password@host. The user part cannot contain ":", so a URL with a user name only
        // (https://org@dev.azure.com) or a port (http://localhost:5000) is not matched.
        public Regex Pattern { get; } = SecretRulePattern.Create(
            @"\b[A-Za-z][A-Za-z0-9+.-]*://[^\s/:@'""]+:(?<secret>[^\s/@'""]+)@[A-Za-z0-9.-]+");
    }
}
