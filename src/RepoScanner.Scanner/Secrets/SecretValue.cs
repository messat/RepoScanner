using System.Linq;
using System.Text.RegularExpressions;

namespace RepoScanner.Scanner.Secrets
{
    // The secret a rule matched: its "secret" group with all whitespace removed. Only SEC001 (a key
    // split over several lines) and SEC003 (spaces around a password) can contain any. Removing it
    // means Windows and Unix line endings give the same secret, and so the same hash.
    public static class SecretValue
    {
        public const string GroupName = "secret";

        public static string FromMatch(Match match)
        {
            string value = match.Groups[GroupName].Value;
            return string.Concat(value.Where(character => !char.IsWhiteSpace(character)));
        }
    }
}
