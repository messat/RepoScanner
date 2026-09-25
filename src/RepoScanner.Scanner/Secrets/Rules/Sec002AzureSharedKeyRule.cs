using System.Text.RegularExpressions;
using RepoScanner.Core.Enums;

namespace RepoScanner.Scanner.Secrets.Rules
{
    // The shared key in an Azure Storage or Service Bus connection string. It gives full access to the
    // account or namespace, so it is Critical (Issue 3 design, Secret rules).
    public sealed class Sec002AzureSharedKeyRule : ISecretRule
    {
        public string RuleId => "SEC002";

        public Severity Severity => Severity.Critical;

        public string Message => "Azure Storage or Service Bus key";

        // At least 40 base64 characters: storage keys are 88 and Service Bus keys 44, while a
        // placeholder such as <your-storage-key> is not base64. "SharedAccessKeyName=" is not matched,
        // because "=" must follow the key name directly.
        public Regex Pattern { get; } = SecretRulePattern.Create(
            @"\b(?:AccountKey|SharedAccessKey)\s*=\s*(?<secret>[A-Za-z0-9+/]{40,}={0,2})",
            RegexOptions.IgnoreCase);
    }
}
