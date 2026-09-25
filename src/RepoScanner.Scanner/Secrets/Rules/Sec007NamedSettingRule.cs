using System.Text.RegularExpressions;
using RepoScanner.Core.Enums;

namespace RepoScanner.Scanner.Secrets.Rules
{
    // A value held in a setting whose name suggests a secret: "Password", "ApiKey", "ClientSecret",
    // "DEVOPS_PAT" and so on. The catch-all after the specific rules, so it is Medium, and the rule the
    // placeholder allow-list matters most for (Issue 3 design, Secret rules).
    public sealed class Sec007NamedSettingRule : ISecretRule
    {
        // The name contains one of these words, or is or ends in "pat" (a personal access token). The
        // lookbehind makes the name start at a word boundary, so the "pat" in "compat" is never a name.
        // (?-i:Pat) is case-sensitive, so "AzureDevOpsPat" counts and "Path" does not.
        private const string SecretName =
            @"(?<![\w.$-])(?<name>" +
            @"[\w.$-]*(?:password|passwd|pwd|secret|token|apikey|api_key|api-key|accesskey|access_key)[\w.$-]*" +
            @"|pat" +
            @"|[\w.$-]*(?-i:Pat)" +
            @"|[\w.$-]*[_.-]pat" +
            @")" +
            // ...and does not end in a word naming something other than the secret, such as TokenEndpoint,
            // PasswordHash, PasswordPattern or ApiKeyHeader
            @"(?<!url|uri|endpoint|name|path|type|length|lifetime|timeout|hash|pattern|regex|header)";

        // At least 8 characters, no whitespace, and not a URL
        private const string QuotedValue = @"(?<secret>(?![^\s""']*://)[^\s""']{8,})";

        // The same for an unquoted value, which also stops at a comment (#) and at ; , ( and ), so a
        // line of code is not read as a value
        private const string UnquotedValue = @"(?<secret>(?![^\s""'#;,()]*://)[^\s""'#;,()]{8,})";

        // "Password": "...", ApiKey = "...", password: '...', $apiKey = @"..." (JSON, YAML, C#, PowerShell)
        private const string QuotedForm = SecretName + @"[""']?\s*[:=]\s*[@$]?[""']" + QuotedValue + @"[""']";

        // <add key="ClientSecret" value="..." /> (.config files)
        private const string XmlForm = @"\bkey\s*=\s*[""']" + SecretName + @"[""']\s+value\s*=\s*[""']" + QuotedValue + @"[""']";

        // NAME=value with no spaces around =, or name: value, at the start of a line (.env, shell and
        // YAML files). A C# assignment has spaces around =, so it is not read as this form.
        private const string UnquotedForm = @"^[ \t]*(?:export[ \t]+)?" + SecretName + @"(?:=|:[ \t]+)" + UnquotedValue;

        public string RuleId => "SEC007";

        public Severity Severity => Severity.Medium;

        public string Message => "Password, secret or key in a named setting";

        // Multiline: ^ in UnquotedForm is the start of any line, not only of the file
        public Regex Pattern { get; } = SecretRulePattern.Create(
            QuotedForm + "|" + XmlForm + "|" + UnquotedForm,
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
    }
}
