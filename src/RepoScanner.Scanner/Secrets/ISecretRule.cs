using System.Text.RegularExpressions;
using RepoScanner.Core.Enums;

namespace RepoScanner.Scanner.Secrets
{
    // One kind of hard-coded secret, SEC001 to SEC007 (Issue 3 design, Secret rules). SecretScanner
    // runs every rule's Pattern over the whole text of each file.
    public interface ISecretRule
    {
        // SEC001 and so on. Also the order the rules run in, so the more specific rule wins an overlap.
        string RuleId { get; }

        Severity Severity { get; }

        // Shown on the dashboard, for example "Private key"
        string Message { get; }

        // Contains a group named "secret" (SecretValue.GroupName): the part that is masked and hashed. It
        // may also contain a group named "name" (SecretValue.NameGroupName): the setting the secret is
        // assigned to. Built with SecretRulePattern.Create, so it has the one-second match timeout.
        Regex Pattern { get; }
    }
}
