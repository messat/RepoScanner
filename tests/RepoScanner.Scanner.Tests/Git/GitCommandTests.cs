using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using RepoScanner.Scanner.Git;

namespace RepoScanner.Scanner.Tests.Git
{
    [TestFixture]
    public class GitCommandTests
    {
        // A fake PAT: values starting "your-" are placeholders to RepoScanner's own self-scan (Issue 3 design)
        private const string Pat = "your-test-pat-0001";

        private const string Url = "https://dev.azure.com/BlacklightInternalsMo/Kudos%20Wall/_git/victim-repo";

        private static readonly string Folder = Path.Combine(Path.GetTempPath(), "reposcanner", "3f9c0a1b7d2e");

        // Azure DevOps takes a PAT as Basic credentials with an empty user name, ":" + PAT in Base64
        private static readonly string Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + Pat));

        // Decision 7: the latest commit of one branch, the sign-in helper off, long paths on, and "--" so neither the
        // URL nor the folder can be read as an option
        [Test]
        public void Clone_HasTheArgumentsOfTheDesign()
        {
            GitCommand command = GitCommand.Clone(Url, "main", Folder, Pat);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(command.Name, Is.EqualTo("clone"));
                Assert.That(command.Arguments, Is.EqualTo(new string[]
                {
                    "-c", "credential.helper=", "-c", "core.longpaths=true",
                    "clone", "--depth", "1", "--single-branch", "--branch", "main", "--", Url, Folder
                }));
            }
        }

        // Success criterion 2 and decision 1: the PAT is in git's environment, never its arguments
        [Test]
        public void Clone_PutsThePatOnlyInTheEnvironment()
        {
            GitCommand command = GitCommand.Clone(Url, "main", Folder, Pat);
            bool leaked = command.Arguments.Any(argument => argument.Contains(Pat, StringComparison.Ordinal) || argument.Contains(Base64, StringComparison.Ordinal));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(leaked, Is.False, "the PAT is in git's arguments");
                Assert.That(command.Environment["GIT_CONFIG_COUNT"], Is.EqualTo("1"));
                Assert.That(command.Environment["GIT_CONFIG_KEY_0"], Is.EqualTo("http.extraheader"));
                Assert.That(command.Environment["GIT_CONFIG_VALUE_0"], Is.EqualTo("AUTHORIZATION: Basic " + Base64));
                Assert.That(command.Secrets, Is.EquivalentTo(new string[] { Pat, Base64 }));
            }
        }

        // Decision 8: a rejected PAT fails in seconds instead of waiting for a login that never comes
        [Test]
        public void Clone_TurnsOffPrompts()
        {
            GitCommand command = GitCommand.Clone(Url, "main", Folder, Pat);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(command.Environment["GIT_TERMINAL_PROMPT"], Is.EqualTo("0"));
                Assert.That(command.Environment["GCM_INTERACTIVE"], Is.EqualTo("never"));
            }
        }

        // The commit ID the clone got (#38): no network, so no PAT, and prompts off like every command
        [Test]
        public void RevParseHead_HasTheArgumentsOfTheDesign()
        {
            GitCommand command = GitCommand.RevParseHead(Folder);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(command.Name, Is.EqualTo("rev-parse"));
                Assert.That(command.Arguments, Is.EqualTo(new string[] { "-C", Folder, "rev-parse", "HEAD" }));
                Assert.That(command.Secrets, Is.Empty);
                Assert.That(command.Environment["GIT_TERMINAL_PROMPT"], Is.EqualTo("0"));
                Assert.That(command.Environment["GCM_INTERACTIVE"], Is.EqualTo("never"));
            }
        }

        // A command in a log line or an exception must not print the environment, which holds the PAT
        [Test]
        public void ToString_ShowsNoSecrets()
        {
            string text = GitCommand.Clone(Url, "main", Folder, Pat).ToString();
            bool leaked = text.Contains(Pat, StringComparison.Ordinal) || text.Contains(Base64, StringComparison.Ordinal);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(text, Does.StartWith("git -c credential.helper= -c core.longpaths=true clone --depth 1"));
                Assert.That(leaked, Is.False, "ToString printed the PAT");
            }
        }
    }
}
