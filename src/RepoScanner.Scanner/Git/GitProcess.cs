using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RepoScanner.Scanner.Git
{
    // Runs a GitCommand with git from PATH: the command line, as ADR-016 decided, not LibGit2Sharp, which has no
    // reliable shallow clone (Issue 8 design, Running git). Singleton: it keeps nothing between commands.
    public sealed class GitProcess
    {
        // Enough to hold git's reason for a failure, which it prints last, without keeping a whole progress log
        private const int StandardErrorLinesKept = 20;

        // After git is stopped, how long to wait for what it printed before letting go of it
        private static readonly TimeSpan OutputAfterStopping = TimeSpan.FromSeconds(5);

        // The clock behind the time limit, so tests can move it forward instead of waiting
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<GitProcess> _logger;

        public GitProcess(TimeProvider timeProvider, ILogger<GitProcess> logger)
        {
            _timeProvider = timeProvider;
            _logger = logger;
        }

        // Returns what git printed to standard output. A non-zero exit throws GitCommandException with git's last
        // lines, every secret replaced by ***. Going past timeLimit (#40) stops git and its child processes and throws
        // TimeoutException; cancelling stops them and throws OperationCanceledException.
        public async Task<string> RunAsync(GitCommand command, TimeSpan timeLimit, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Started before git is, so the limit counts from the moment git could start working
            using (CancellationTokenSource limit = new CancellationTokenSource(timeLimit, _timeProvider))
            using (CancellationTokenSource stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token))
            using (Process process = new Process { StartInfo = CreateStartInfo(command) })
            {
                process.Start();

                // Git never needs input here: closed at once, so it can never wait for any
                process.StandardInput.Close();

                // Both read while git runs, so a full pipe can never stall it
                Task<string> output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
                Task<string> errors = LastLinesAsync(process.StandardError);

                try
                {
                    await process.WaitForExitAsync(stopping.Token);
                }
                catch (OperationCanceledException)
                {
                    Stop(process, command);
                    await LetGoOfOutputAsync(output, errors);

                    // The limit, not the caller: a failed command the caller can report, rather than a stopped run
                    if (limit.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException("git " + command.Name + " took longer than its time limit of " + timeLimit + " and was stopped");
                    }

                    throw;
                }

                string standardOutput = Wipe(await output, command.Secrets);
                string standardError = Wipe(await errors, command.Secrets);

                if (process.ExitCode != 0)
                {
                    throw new GitCommandException(command.Name, process.ExitCode, standardError);
                }

                return standardOutput;
            }
        }

        // Internal so tests can check exactly what git is started with, without starting it
        internal static ProcessStartInfo CreateStartInfo(GitCommand command)
        {
            ProcessStartInfo info = new ProcessStartInfo("git")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            // One argument each, quoted by .NET as the platform needs, so a folder with a space stays one argument
            foreach (string argument in command.Arguments)
            {
                info.ArgumentList.Add(argument);
            }

            // On git's process only, added to what this process already has; this process's own environment is untouched
            foreach (KeyValuePair<string, string> variable in command.Environment)
            {
                info.Environment[variable.Key] = variable.Value;
            }

            return info;
        }

        // Git and every process it started, such as git-remote-https, which would otherwise carry on downloading
        private void Stop(Process process, GitCommand command)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
            catch (InvalidOperationException)
            {
                // It had already finished: nothing to stop
            }
            catch (Win32Exception exception)
            {
                _logger.LogWarning("Could not stop git {Command}: {Reason}", command.Name, exception.Message);
            }
        }

        // Once git is stopped its pipes close, so this is quick. Capped, so a process that could not be stopped never
        // holds the run, and whatever the readers end with is of no interest: the run is already being stopped.
        private static async Task LetGoOfOutputAsync(Task<string> output, Task<string> errors)
        {
            try
            {
                await Task.WhenAll(output, errors).WaitAsync(OutputAfterStopping, CancellationToken.None);
            }
            catch (Exception exception) when (exception is TimeoutException || exception is IOException || exception is ObjectDisposedException)
            {
                // Stopped part-way through: what it printed is not needed
            }
        }

        // The last lines git printed to standard error, without blank ones
        private static async Task<string> LastLinesAsync(StreamReader reader)
        {
            Queue<string> lines = new Queue<string>();
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (line.Trim().Length == 0)
                {
                    continue;
                }

                lines.Enqueue(line);
                if (lines.Count > StandardErrorLinesKept)
                {
                    lines.Dequeue();
                }
            }

            return string.Join("\n", lines);
        }

        // Longest first, so a secret that contains another is wiped whole rather than left partly showing
        private static string Wipe(string text, IReadOnlyList<string> secrets)
        {
            foreach (string secret in secrets.Where(secret => secret.Length > 0).OrderByDescending(secret => secret.Length))
            {
                text = text.Replace(secret, "***", StringComparison.Ordinal);
            }

            return text;
        }
    }
}
