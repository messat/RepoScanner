using System;
using System.Collections.Generic;
using System.Text;

namespace RepoScanner.Scanner.Git
{
    // One git command: its arguments, the environment variables set on its process only, and the values to wipe from
    // anything it prints (Issue 8 design, Running git). Built here and run by GitProcess, so tests can check where the
    // PAT goes without running git. A class, not a record: C# keeps the name Clone for a record's own copying.
    public sealed class GitCommand
    {
        public GitCommand(string name, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, IReadOnlyList<string> secrets)
        {
            Name = name;
            Arguments = arguments;
            Environment = environment;
            Secrets = secrets;
        }

        // What a failure message calls the command, such as "clone"
        public string Name { get; }

        public IReadOnlyList<string> Arguments { get; }

        // Set on git's process only, on top of what this process has
        public IReadOnlyDictionary<string, string> Environment { get; }

        // Values replaced by *** in anything git prints
        public IReadOnlyList<string> Secrets { get; }

        // The latest commit of one branch (decision 7), with the PAT in environment variables only (decision 1), sent only
        // to organizationUrl, which url is inside
        public static GitCommand Clone(string organizationUrl, string url, string branch, string folder, string pat)
        {
            // Azure DevOps takes a PAT as Basic credentials with an empty user name
            string credential = Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + pat));

            // Read by git exactly as -c http.<organisation>/.extraheader=... would be (Git 2.31 and later), but never shown
            // in a process list, and never saved in the clone's .git/config. Scoped to the organisation, so the header
            // goes to no other server, such as one a repository's .lfsconfig names (final review). Git matches the scope
            // as a prefix, so every repository in the organisation gets it.
            Dictionary<string, string> environment = EveryCommandEnvironment();
            environment["GIT_CONFIG_COUNT"] = "1";
            environment["GIT_CONFIG_KEY_0"] = "http." + organizationUrl.TrimEnd('/') + "/.extraheader";
            environment["GIT_CONFIG_VALUE_0"] = "AUTHORIZATION: Basic " + credential;

            string[] arguments =
            {
                // No sign-in helper, such as Git Credential Manager on Windows: a rejected PAT must fail, not open a
                // sign-in window or quietly use the developer's own saved login. It holds no secret, so it may be here.
                "-c", "credential.helper=",
                // Deep paths clone on Windows; ignored elsewhere
                "-c", "core.longpaths=true",
                "clone", "--depth", "1", "--single-branch", "--branch", branch,
                // Nothing after this can be read as an option
                "--", url, folder
            };

            // The Base64 form is wiped too: it is what git would print if a developer had tracing switched on
            return new GitCommand("clone", arguments, environment, new string[] { pat, credential });
        }

        // The commit a clone got (#38). -C runs it in the clone without changing this process's folder. It reads the
        // clone on disk, so it needs no PAT.
        public static GitCommand RevParseHead(string folder)
        {
            return new GitCommand("rev-parse", new string[] { "-C", folder, "rev-parse", "HEAD" }, EveryCommandEnvironment(), Array.Empty<string>());
        }

        // The arguments only, never the environment, which holds the PAT
        public override string ToString()
        {
            return "git " + string.Join(" ", Arguments);
        }

        // Every command: git never waits for input that cannot come, so a rejected PAT fails in seconds (decision 8), and
        // never downloads Git LFS content
        private static Dictionary<string, string> EveryCommandEnvironment()
        {
            return new Dictionary<string, string>
            {
                // No user name or password prompt in a terminal
                ["GIT_TERMINAL_PROMPT"] = "0",
                // No Git Credential Manager window, should a helper still be configured somewhere
                ["GCM_INTERACTIVE"] = "never",
                // LFS files stay as their small pointer files (final review). Git for Windows switches LFS on, and LFS
                // would fetch from whatever server a repository's .lfsconfig names, and fill the replica's 2 GiB disk.
                ["GIT_LFS_SKIP_SMUDGE"] = "1"
            };
        }
    }
}
