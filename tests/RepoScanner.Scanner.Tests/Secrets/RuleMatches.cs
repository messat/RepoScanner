using System.Collections.Generic;
using System.Linq;
using RepoScanner.Scanner.Secrets;

namespace RepoScanner.Scanner.Tests.Secrets
{
    // What a rule finds in some text, read the way SecretScanner reads it
    internal static class RuleMatches
    {
        public static IReadOnlyList<string> SecretsFoundBy(ISecretRule rule, string text)
        {
            return rule.Pattern.Matches(text).Select(SecretValue.FromMatch).ToList();
        }
    }
}
