using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Testing;
using NUnit.Framework;
using RepoScanner.Scanner.Git;

namespace RepoScanner.Scanner.Tests.Git
{
    // Runs the real git, which the scanner needs anyway: these tests need it on PATH, but no network
    [TestFixture]
    public class GitProcessTests
    {
        // A fake PAT: values starting "your-" are placeholders to RepoScanner's own self-scan (Issue 3 design)
        private const string Pat = "your-test-pat-0001";

        // The arguments go through ArgumentList, so nothing is re-quoted, and only the command's variables are added
        [Test]
        public void CreateStartInfo_UsesTheCommandsArgumentsAndEnvironment()
        {
            GitCommand command = Command("version", "-c", "core.longpaths=true", "--version");

            ProcessStartInfo info = GitProcess.CreateStartInfo(command);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(info.FileName, Is.EqualTo("git"));
                Assert.That(info.ArgumentList, Is.EqualTo(command.Arguments));
                Assert.That(info.Arguments, Is.Empty);
                Assert.That(info.UseShellExecute, Is.False);
                Assert.That(info.RedirectStandardInput, Is.True);
                Assert.That(info.RedirectStandardOutput, Is.True);
                Assert.That(info.RedirectStandardError, Is.True);
                foreach (KeyValuePair<string, string> variable in command.Environment)
                {
                    Assert.That(info.Environment[variable.Key], Is.EqualTo(variable.Value), variable.Key);
                }
            }
        }

        [Test]
        public async Task RunAsync_ReturnsWhatGitPrinted()
        {
            string output = await Git().RunAsync(Command("version", "--version"), CancellationToken.None);

            Assert.That(output, Does.StartWith("git version"));
        }

        // The failure becomes the scan run's error, so it must say what failed and never carry the PAT
        [Test]
        public void RunAsync_WhenGitFails_ThrowsWithItsExitCodeAndLastLinesWithSecretsWiped()
        {
            GitCommand failing = Command("fail", "-c", "alias.fail=!echo first line >&2; echo " + Pat + " >&2; exit 3", "fail");

            GitCommandException exception = Assert.ThrowsAsync<GitCommandException>(() => Git().RunAsync(failing, CancellationToken.None));
            bool leaked = exception.Message.Contains(Pat, StringComparison.Ordinal) || exception.StandardError.Contains(Pat, StringComparison.Ordinal);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(exception.ExitCode, Is.EqualTo(3));
                Assert.That(exception.Message, Is.EqualTo("git fail failed with exit code 3: first line ***"));
                Assert.That(leaked, Is.False, "the PAT was not wiped");
            }
        }

        // git starts helper processes (here sh and sleep). If one outlived git, its open output pipe would keep RunAsync
        // waiting until GitProcess gives up on the output after 5 seconds, so 4 seconds shows they were all stopped.
        [Test]
        public async Task RunAsync_WhenCancelled_StopsGitAndItsChildQuickly()
        {
            GitCommand slow = Command("slow", "-c", "alias.slow=!sleep 60", "slow");
            Stopwatch elapsed = Stopwatch.StartNew();

            using (CancellationTokenSource stopping = new CancellationTokenSource(TimeSpan.FromMilliseconds(500)))
            {
                await Assert.ThatAsync(() => Git().RunAsync(slow, stopping.Token), Throws.InstanceOf<OperationCanceledException>());
            }

            Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds(4)));
        }

        private static GitProcess Git()
        {
            return new GitProcess(new FakeLogger<GitProcess>());
        }

        // A command as GitCommand builds them: prompts off, and the fake PAT as the secret to wipe
        private static GitCommand Command(string name, params string[] arguments)
        {
            Dictionary<string, string> environment = new Dictionary<string, string>
            {
                ["GIT_TERMINAL_PROMPT"] = "0",
                ["GCM_INTERACTIVE"] = "never"
            };

            return new GitCommand(name, arguments, environment, new string[] { Pat });
        }
    }
}
