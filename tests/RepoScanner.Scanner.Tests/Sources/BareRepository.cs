using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace RepoScanner.Scanner.Tests.Sources
{
    // Bare git repositories on disk, laid out as Azure DevOps addresses them: <root>/<project>/_git/<repository>. So
    // file:///<root> is an organisation URL, and the real CloneUrl finds them, with no network (Issue 8 design, Testing).
    internal static class BareRepository
    {
        // Git settings from the developer's machine (commit signing, hooks, line-ending conversion, ignore rules) must
        // not change a test, so every command here reads this empty file in place of their own
        private static readonly Lazy<string> EmptyGlobalConfig = new Lazy<string>(() =>
        {
            string path = Path.Combine(Path.GetTempPath(), "reposcanner-tests", "empty.gitconfig");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Empty);
            return path;
        });

        // <root>/<project>/_git/<repository> with two commits on branch: the first adds first.txt, the second every file
        // in contentFolder. Returns the second commit's ID, the one a clone of the branch gets.
        public static string Create(string root, string projectName, string repositoryName, string branch, string contentFolder)
        {
            using (TempFolder work = new TempFolder())
            {
                Git(work.FullPath, "init", "--quiet", "--initial-branch", branch);
                File.WriteAllText(Path.Combine(work.FullPath, "first.txt"), "first commit");
                Commit(work.FullPath, "First commit");

                CopyFolder(contentFolder, work.FullPath);
                Commit(work.FullPath, "Second commit");

                string bare = BarePath(root, projectName, repositoryName);
                Directory.CreateDirectory(Path.GetDirectoryName(bare)!);
                Git(root, "clone", "--quiet", "--bare", work.FullPath, bare);

                return Git(bare, "rev-parse", "HEAD").Trim();
            }
        }

        // A repository with no commits yet, as a new Azure DevOps repository is
        public static void CreateEmpty(string root, string projectName, string repositoryName)
        {
            string bare = BarePath(root, projectName, repositoryName);
            Directory.CreateDirectory(bare);
            Git(bare, "init", "--quiet", "--bare");
        }

        // file:///C:/... with spaces escaped, used as Scanner:AzureDevOps:OrganizationUrl
        public static string OrganizationUrl(string root)
        {
            return new Uri(root).AbsoluteUri;
        }

        // Runs git in folder, isolated from the developer's own git settings, and returns what it printed. Throws when
        // git fails, so a broken test set-up is never mistaken for the code under test failing.
        public static string Git(string folder, params string[] arguments)
        {
            ProcessStartInfo info = new ProcessStartInfo("git")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in new string[]
            {
                "-C", folder,
                "-c", "user.name=RepoScanner Tests", "-c", "user.email=tests@example.com",
                "-c", "core.longpaths=true", "-c", "core.autocrlf=false"
            })
            {
                info.ArgumentList.Add(argument);
            }

            foreach (string argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            info.Environment["GIT_CONFIG_GLOBAL"] = EmptyGlobalConfig.Value;

            using (Process process = Process.Start(info)!)
            {
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                Task<string> errors = process.StandardError.ReadToEndAsync();
                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException("git " + string.Join(" ", arguments) + " failed in the test set-up: " + errors.Result);
                }

                return output.Result;
            }
        }

        private static string BarePath(string root, string projectName, string repositoryName)
        {
            return Path.Combine(root, projectName, "_git", repositoryName);
        }

        // Every file, including ignored and hidden ones such as .reposcanner-exclude, so the clone holds exactly the content
        private static void Commit(string folder, string message)
        {
            Git(folder, "add", "--all", "--force");
            Git(folder, "commit", "--quiet", "--no-verify", "--message", message);
        }

        private static void CopyFolder(string from, string to)
        {
            EnumerationOptions everyFile = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (string file in Directory.EnumerateFiles(from, "*", everyFile))
            {
                string target = Path.Combine(to, Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        }
    }
}
