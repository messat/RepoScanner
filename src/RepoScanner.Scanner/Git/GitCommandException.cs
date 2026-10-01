using System;
using System.Linq;

namespace RepoScanner.Scanner.Git
{
    // Git exited with an error. The message becomes the scan run's error, such as "git clone failed with exit code 128:
    // remote: TF401019: The Git repository with name or identifier ghost does not exist ...", so it ends with git's
    // own reason. Every secret has already been wiped from what git printed (Issue 8 design, Running git).
    public sealed class GitCommandException : Exception
    {
        // Git's reason comes last; three lines hold it without the progress lines before it
        private const int LinesInMessage = 3;

        public GitCommandException(string commandName, int exitCode, string standardError)
            : base(BuildMessage(commandName, exitCode, standardError))
        {
            ExitCode = exitCode;
            StandardError = standardError;
        }

        public int ExitCode { get; }

        // The last lines git printed to standard error, secrets already wiped. The clone source reads it to tell a
        // rejected PAT from any other failure.
        public string StandardError { get; }

        private static string BuildMessage(string commandName, int exitCode, string standardError)
        {
            string[] lines = standardError
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .TakeLast(LinesInMessage)
                .ToArray();

            string message = "git " + commandName + " failed with exit code " + exitCode;
            return lines.Length == 0 ? message : message + ": " + string.Join(" ", lines);
        }
    }
}
