using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace RepoScanner.Scanner.Secrets
{
    // The secret a rule matched: its "secret" group with all whitespace, and the escapes \n and \r,
    // removed. Only SEC001 (a key split over several lines, or held in a JSON string) and SEC003 (spaces
    // around a password) can contain any. Removing them means Windows and Unix line endings, and JSON
    // escaping, give the same secret, and so the same hash.
    public static class SecretValue
    {
        public const string GroupName = "secret";

        // SEC003 and SEC007 also capture the name of the setting the secret is assigned to, so the
        // allow-list can drop a value that only repeats it (Password=$password)
        public const string NameGroupName = "name";

        public static string FromMatch(Match match)
        {
            string value = match.Groups[GroupName].Value
                .Replace(@"\n", string.Empty, StringComparison.Ordinal)
                .Replace(@"\r", string.Empty, StringComparison.Ordinal);

            return string.Concat(value.Where(character => !char.IsWhiteSpace(character)));
        }
    }
}
