using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging.Testing;

namespace RepoScanner.Scanner.Tests.Logging
{
    // Reads what GetStructuredStateValue cannot: a value from the scopes a line was logged in (#62), and a template
    // value as the object that was passed in rather than as text, such as a dictionary (#64)
    internal static class LogRecordValues
    {
        // The innermost scope holding the name wins, as Serilog would give it; null when no scope holds it
        public static object? Scope(FakeLogRecord record, string name)
        {
            foreach (object? scope in record.Scopes.Reverse())
            {
                if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                {
                    foreach (KeyValuePair<string, object?> pair in pairs)
                    {
                        if (pair.Key == name)
                        {
                            return pair.Value;
                        }
                    }
                }
            }

            return null;
        }

        // The value given for {name} in the line's template; null when the template has no such name
        public static object? State(FakeLogRecord record, string name)
        {
            if (record.State is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (KeyValuePair<string, object?> pair in pairs)
                {
                    if (pair.Key == name)
                    {
                        return pair.Value;
                    }
                }
            }

            return null;
        }
    }
}
