using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RepoScanner.Scanner.Secrets
{
    // Values that look like secrets to a rule but are obviously not real ones: sample words, references
    // to a value kept somewhere else, filler, and a value that only repeats its setting's name. Only whole
    // values count, so "changeme123" is still reported (Issue 3 design, Placeholder allow-list).
    public static class PlaceholderAllowList
    {
        private static readonly HashSet<string> Words = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "changeme", "change-me", "password", "secret", "placeholder", "redacted", "todo", "none", "null", "example", "dummy"
        };

        // ${NAME} and $(NAME) (shell, MSBuild and pipeline variables), #{NAME}# (Azure Pipelines token
        // replacement), %NAME% (Windows environment variables), {{NAME}} (templates), {NAME} (C#
        // interpolation and string.Format), __NAME__, <NAME>, $env:NAME (PowerShell) and $NAME in capitals,
        // digits and underscores (shell and pipeline variables). A lower or mixed case $name is not
        // included, because "$uperSecret1" could be a real password.
        private static readonly Regex Reference = SecretRulePattern.Create(
            @"^(?:\$\{.+\}|\$\(.+\)|#\{.+\}#|%.+%|\{\{.+\}\}|\{[^{}]+\}|__.+__|<.+>|\$(?i:env):\w+|\$[A-Z_][A-Z0-9_]*)$");

        // settingName is the name of the setting the value is assigned to, when the rule captures it
        // (SEC003 and SEC007); null otherwise
        public static bool IsPlaceholder(string value, string? settingName = null)
        {
            string trimmed = value.Trim();

            // An empty value has nothing to report
            return trimmed.Length == 0
                || Words.Contains(trimmed)
                || Reference.IsMatch(trimmed)
                || IsOneCharacterRepeated(trimmed)
                || trimmed.StartsWith("your-", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("your_", StringComparison.OrdinalIgnoreCase)
                || IsTheSettingsOwnName(trimmed, settingName);
        }

        // xxxxxxxx, ********, 00000000: filler, not a secret. One character on its own is not filler.
        private static bool IsOneCharacterRepeated(string value)
        {
            char first = char.ToLowerInvariant(value[0]);
            return value.Length >= 2 && value.All(character => char.ToLowerInvariant(character) == first);
        }

        // Password=$password or AcquireToken: "acquireToken": the value only repeats the setting's name, so
        // it is a variable or a constant, not a secret. Compared ignoring case and anything but letters and
        // digits, so $, braces, hyphens and underscores do not matter.
        private static bool IsTheSettingsOwnName(string value, string? settingName)
        {
            if (settingName == null)
            {
                return false;
            }

            string valueLetters = LettersAndDigits(value);
            return valueLetters.Length > 0 && valueLetters == LettersAndDigits(settingName);
        }

        private static string LettersAndDigits(string text)
        {
            return string.Concat(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant));
        }
    }
}
