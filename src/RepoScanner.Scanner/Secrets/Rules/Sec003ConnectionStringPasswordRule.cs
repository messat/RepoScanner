using System.Text.RegularExpressions;
using RepoScanner.Core.Enums;

namespace RepoScanner.Scanner.Secrets.Rules
{
    // A password in a database connection string (SQL Server, PostgreSQL, MySQL and so on). It is
    // exactly what ADR-007 says this tool must flag, and it is High (Issue 3 design, Secret rules).
    public sealed class Sec003ConnectionStringPasswordRule : ISecretRule
    {
        // One key=value; pair. Quotes are excluded, so a match stays inside one string literal.
        private const string Pair = @"[^;'""\r\n]*;";

        // A key that says this is a connection string to a server
        private const string ServerKey = @"(?:Server|Data\s*Source|Host|Address)\s*=";

        // The password is the value, up to ; or a quote or the end of the line. The key is captured as the
        // setting's name, so the allow-list drops Password=$password. (Named ...Pattern, which SEC007
        // leaves out, so this repository's own scan does not report the regex as a secret.)
        private const string PasswordPattern = @"(?<name>Password|Pwd)\s*=\s*(?<secret>[^;'""\r\n]+)";

        // The server usually comes first; the password can come first too
        private const string ServerFirst = @"\b" + ServerKey + Pair + "(?:" + Pair + @")*?\s*" + PasswordPattern;
        private const string PasswordFirst = @"\b" + PasswordPattern + ";(?:" + Pair + @")*?\s*" + ServerKey;

        public string RuleId => "SEC003";

        public Severity Severity => Severity.High;

        public string Message => "Password in a connection string";

        public Regex Pattern { get; } = SecretRulePattern.Create(ServerFirst + "|" + PasswordFirst, RegexOptions.IgnoreCase);
    }
}
