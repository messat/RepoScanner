using System;

namespace RepoScanner.Scanner.Local
{
    // Finds --path <folder> or --path=<folder> in the command-line arguments (#20). Read from the
    // arguments rather than IConfiguration: the host adds every environment variable to configuration,
    // so Windows' own PATH variable would appear as the setting "path" on every run (design decision 9).
    public static class LocalPathArgument
    {
        public const string Name = "--path";

        // True when --path is present. folder is what follows it, or null when nothing does; an
        // argument starting "--" is another setting, not a folder.
        public static bool TryGet(string[] args, out string? folder)
        {
            for (int index = 0; index < args.Length; index++)
            {
                string argument = args[index];

                if (string.Equals(argument, Name, StringComparison.OrdinalIgnoreCase))
                {
                    bool hasValue = index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal);
                    folder = hasValue ? args[index + 1] : null;
                    return true;
                }

                if (argument.StartsWith(Name + "=", StringComparison.OrdinalIgnoreCase))
                {
                    string value = argument.Substring(Name.Length + 1);
                    folder = value.Length > 0 ? value : null;
                    return true;
                }
            }

            folder = null;
            return false;
        }
    }
}
