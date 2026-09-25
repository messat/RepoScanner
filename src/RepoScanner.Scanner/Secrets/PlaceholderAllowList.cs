using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RepoScanner.Scanner.Secrets
{
    // Values that look like secrets to a rule but are obviously not real ones: sample words, references
    // to a value kept somewhere else, and filler. Only whole values count, so "changeme123" is still
    // reported (Issue 3 design, Placeholder allow-list).
    public static class PlaceholderAllowList
    {
        private static readonly HashSet<string> Words = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "changeme", "change-me", "password", "secret", "placeholder", "redacted", "todo", "none", "null", "example", "dummy"
        };

        // ${NAME} and $(NAME) (shell, MSBuild and pipeline variables), #{NAME}# (Azure Pipelines token
        // replacement), %NAME% (Windows environment variables), {{NAME}} (templates), __NAME__ and <NAME>
        private static readonly Regex Reference = SecretRulePattern.Create(
            @"^(?:\$\{.+\}|\$\(.+\)|#\{.+\}#|%.+%|\{\{.+\}\}|__.+__|<.+>)$");

        public static bool IsPlaceholder(string value)
        {
            string trimmed = value.Trim();

            // An empty value has nothing to report
            return trimmed.Length == 0
                || Words.Contains(trimmed)
                || Reference.IsMatch(trimmed)
                || IsOneCharacterRepeated(trimmed)
                || trimmed.StartsWith("your-", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("your_", StringComparison.OrdinalIgnoreCase);
        }

        // xxxxxxxx, ********, 00000000: filler, not a secret. One character on its own is not filler.
        private static bool IsOneCharacterRepeated(string value)
        {
            char first = char.ToLowerInvariant(value[0]);
            return value.Length >= 2 && value.All(character => char.ToLowerInvariant(character) == first);
        }
    }
}
