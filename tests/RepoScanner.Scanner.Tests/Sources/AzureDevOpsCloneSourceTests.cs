using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using RepoScanner.Scanner.Configuration;
using RepoScanner.Scanner.Git;
using RepoScanner.Scanner.Queue;
using RepoScanner.Scanner.Sources;

namespace RepoScanner.Scanner.Tests.Sources
{
    // The clone (#37, Issue 8 design, The clone) with the real git, against bare repositories on disk and a stand-in for
    // Azure DevOps on 127.0.0.1: no network
    [TestFixture]
    public class AzureDevOpsCloneSourceTests
    {
        // A fake PAT: values starting "your-" are placeholders to RepoScanner's own self-scan (Issue 3 design)
        private const string Pat = "your-test-pat-0001";

        // What git sends: Basic credentials with an empty user name
        private static readonly string Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + Pat));

        private TempFolder _repos = null!;
        private TempFolder _temp = null!;
        private WorkspaceFolders _folders = null!;
        private FakeLogger<AzureDevOpsCloneSource> _logger = null!;

        // The clone's time limit runs on this clock, so a test can move it 8 minutes on instead of waiting
        private FakeTimeProvider _time = null!;

        [SetUp]
        public void SetUp()
        {
            _time = new FakeTimeProvider();
            _repos = new TempFolder();
            _temp = new TempFolder();
            _folders = new WorkspaceFolders(Path.Combine(_temp.FullPath, "reposcanner"), NullLogger<WorkspaceFolders>.Instance);
            _logger = new FakeLogger<AzureDevOpsCloneSource>();
        }

        [TearDown]
        public void TearDown()
        {
            _temp.Dispose();
            _repos.Dispose();
        }

        [Test]
        public async Task OpenAsync_ClonesTheBranchWithOnlyItsLatestCommit()
        {
            CreateVictimRepo();

            await using (RepositoryWorkspace workspace = await Source(BareRepository.OrganizationUrl(_repos.FullPath))
                .OpenAsync(Request("Kudos Wall", "victim-repo", "main"), CancellationToken.None))
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(Path.GetDirectoryName(workspace.Folder), Is.EqualTo(_folders.Root));
                    Assert.That(File.ReadAllText(Path.Combine(workspace.Folder, "README.md")), Does.StartWith("second commit"));
                    Assert.That(BareRepository.Git(workspace.Folder, "rev-list", "--count", "HEAD").Trim(), Is.EqualTo("1"));
                }
            }
        }

        // Success criterion 3, for a scan that ran: closing the workspace deletes the clone
        [Test]
        public async Task OpenAsync_ClosingTheWorkspace_DeletesTheClone()
        {
            CreateVictimRepo();

            RepositoryWorkspace workspace = await Source(BareRepository.OrganizationUrl(_repos.FullPath))
                .OpenAsync(Request("Kudos Wall", "victim-repo", "main"), CancellationToken.None);
            await workspace.DisposeAsync();

            Assert.That(Directory.Exists(workspace.Folder), Is.False);
        }

        // Success criterion 2: no file holds the PAT either
        [Test]
        public async Task OpenAsync_NeverWritesThePatIntoTheClone()
        {
            CreateVictimRepo();

            await using (RepositoryWorkspace workspace = await Source(BareRepository.OrganizationUrl(_repos.FullPath))
                .OpenAsync(Request("Kudos Wall", "victim-repo", "main"), CancellationToken.None))
            {
                string config = File.ReadAllText(Path.Combine(workspace.Folder, ".git", "config"));
                bool leaked = config.Contains(Pat, StringComparison.Ordinal) || config.Contains(Base64, StringComparison.Ordinal);

                Assert.That(leaked, Is.False, "the clone's .git/config holds the PAT");
            }
        }

        // Decision 14: an ordinary failed scan, with git's reason as the scan run's error
        [Test]
        public void OpenAsync_ForAMissingBranch_FailsWithGitsReasonAndLeavesNoFolder()
        {
            CreateVictimRepo();

            GitCommandException exception = Assert.ThrowsAsync<GitCommandException>(
                () => Source(BareRepository.OrganizationUrl(_repos.FullPath)).OpenAsync(Request("Kudos Wall", "victim-repo", "release"), CancellationToken.None));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(exception.Message, Does.StartWith("git clone failed with exit code 128:"));
                Assert.That(Directory.EnumerateFileSystemEntries(_folders.Root), Is.Empty);
            }
        }

        // A renamed or deleted repository, as the Issue 7 design expected
        [Test]
        public void OpenAsync_ForAMissingRepository_FailsAndLeavesNoFolder()
        {
            CreateVictimRepo();

            Assert.ThrowsAsync<GitCommandException>(
                () => Source(BareRepository.OrganizationUrl(_repos.FullPath)).OpenAsync(Request("Kudos Wall", "ghost", "main"), CancellationToken.None));

            Assert.That(Directory.EnumerateFileSystemEntries(_folders.Root), Is.Empty);
        }

        // Review Focus 4: a new Azure DevOps repository has no commits yet. Not a rejected token, so the run goes on.
        [Test]
        public void OpenAsync_ForAnEmptyRepository_FailsWithGitsReasonAndLeavesNoFolder()
        {
            BareRepository.CreateEmpty(_repos.FullPath, "Kudos Wall", "ProblemApp");

            GitCommandException exception = Assert.ThrowsAsync<GitCommandException>(
                () => Source(BareRepository.OrganizationUrl(_repos.FullPath)).OpenAsync(Request("Kudos Wall", "ProblemApp", "main"), CancellationToken.None));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(exception.Message, Does.Contain("Remote branch main not found"));
                Assert.That(Directory.EnumerateFileSystemEntries(_folders.Root), Is.Empty);
            }
        }

        // Decision 13, against a real git: the server refuses, git cannot ask for a login, and the source names the problem
        [Test]
        public void OpenAsync_WhenAzureDevOpsRefusesTheToken_ThrowsAuthenticationExceptionAndLeavesNoFolder()
        {
            using (FakeAzureDevOps server = FakeAzureDevOps.StartRefusing())
            {
                AzureDevOpsAuthenticationException exception = Assert.ThrowsAsync<AzureDevOpsAuthenticationException>(
                    () => Source(server.OrganizationUrl).OpenAsync(Request("Kudos Wall", "victim-repo", "main"), CancellationToken.None));

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(exception.Message, Is.EqualTo("Azure DevOps refused the token in Scanner:AzureDevOps:Pat for " + server.OrganizationUrl
                        + ". Check that it has not expired or been revoked, and that it has Code (Read)."));

                    // Decision 1 works end to end: the header from git's environment reached its request
                    Assert.That(server.AuthorizationHeaders, Has.Some.EqualTo("Basic " + Base64));
                    Assert.That(Directory.EnumerateFileSystemEntries(_folders.Root), Is.Empty);
                }
            }
        }

        // Review Focus 5: with tracing on, git prints its request headers. Set for this process, so git inherits it.
        [Test]
        public void OpenAsync_WithGitTracingOn_NeverReportsThePat()
        {
            string? traceCurl = Environment.GetEnvironmentVariable("GIT_TRACE_CURL");
            string? curlVerbose = Environment.GetEnvironmentVariable("GIT_CURL_VERBOSE");
            Environment.SetEnvironmentVariable("GIT_TRACE_CURL", "1");
            Environment.SetEnvironmentVariable("GIT_CURL_VERBOSE", "1");
            try
            {
                using (FakeAzureDevOps server = FakeAzureDevOps.StartRefusing())
                {
                    AzureDevOpsAuthenticationException exception = Assert.ThrowsAsync<AzureDevOpsAuthenticationException>(
                        () => Source(server.OrganizationUrl).OpenAsync(Request("Kudos Wall", "victim-repo", "main"), CancellationToken.None));

                    // ToString includes the inner GitCommandException, with what git printed
                    string reported = exception.ToString() + string.Join("\n", _logger.Collector.GetSnapshot().Select(record => record.Message));
                    bool leaked = reported.Contains(Pat, StringComparison.Ordinal) || reported.Contains(Base64, StringComparison.Ordinal);

                    Assert.That(leaked, Is.False, "the PAT reached an exception or a log line");
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("GIT_TRACE_CURL", traceCurl);
                Environment.SetEnvironmentVariable("GIT_CURL_VERBOSE", curlVerbose);
            }
        }

        // The per-repository limit and the job stopping both cancel the clone: git stops, and its folder goes
        [Test]
        public async Task OpenAsync_WhenCancelled_StopsTheCloneAndLeavesNoFolder()
        {
            using (FakeAzureDevOps server = FakeAzureDevOps.StartStalling())
            using (CancellationTokenSource stopping = new CancellationTokenSource())
            {
                Task<RepositoryWorkspace> opening = Source(server.OrganizationUrl).OpenAsync(Request("Kudos Wall", "victim-repo", "main"), stopping.Token);
                await server.FirstRequest.WaitAsync(TimeSpan.FromSeconds(30));

                await stopping.CancelAsync();

                await Assert.ThatAsync(() => opening, Throws.InstanceOf<OperationCanceledException>());
                Assert.That(Directory.EnumerateFileSystemEntries(_folders.Root), Is.Empty);
            }
        }

        // #40, ADR-016's eight-minute cap: a server that never answers cannot hold the clone longer, and the scan run's
        // error names the setting to change
        [Test]
        public async Task OpenAsync_WhenTheCloneTakesLongerThanTheLimit_SaysSoAndLeavesNoFolder()
        {
            using (FakeAzureDevOps server = FakeAzureDevOps.StartStalling())
            {
                Task<RepositoryWorkspace> opening = Source(server.OrganizationUrl).OpenAsync(Request("Kudos Wall", "victim-repo", "main"), CancellationToken.None);
                await server.FirstRequest.WaitAsync(TimeSpan.FromSeconds(30));

                _time.Advance(TimeSpan.FromMinutes(8));

                TimeoutException exception = Assert.ThrowsAsync<TimeoutException>(() => opening);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(exception.Message, Is.EqualTo("The clone took longer than 8 minutes and was stopped (Scanner:AzureDevOps:CloneTimeoutMinutes)"));
                    Assert.That(Directory.EnumerateFileSystemEntries(_folders.Root), Is.Empty);
                }
            }
        }

        // Review Focus 2: a Windows user name such as "Jo Smith" puts a space in %TEMP%
        [Test]
        public async Task OpenAsync_WithASpaceInTheWorkspacePath_Clones()
        {
            CreateVictimRepo();
            _folders = new WorkspaceFolders(Path.Combine(_temp.FullPath, "work space", "reposcanner"), NullLogger<WorkspaceFolders>.Instance);

            await using (RepositoryWorkspace workspace = await Source(BareRepository.OrganizationUrl(_repos.FullPath))
                .OpenAsync(Request("Kudos Wall", "victim-repo", "main"), CancellationToken.None))
            {
                Assert.That(File.Exists(Path.Combine(workspace.Folder, "README.md")), Is.True);
            }
        }

        // Review Focus 3: deep folders go past Windows' old 260-character limit once inside %TEMP%\reposcanner\<name>
        [Test, Platform(Include = "Win")]
        public async Task OpenAsync_WithAPathLongerThan260Characters_ClonesAndDeletes()
        {
            string relative = string.Join("/", Enumerable.Range(1, 6).Select(number => "a-folder-name-that-is-long-" + number.ToString("00", System.Globalization.CultureInfo.InvariantCulture))) + "/deep.txt";
            string content = Path.GetDirectoryName(_temp.WriteText("content/README.md", "second commit"))!;
            _temp.WriteText("content/" + relative, "deep");
            BareRepository.Create(_repos.FullPath, "Kudos Wall", "victim-repo", "main", content);

            RepositoryWorkspace workspace = await Source(BareRepository.OrganizationUrl(_repos.FullPath))
                .OpenAsync(Request("Kudos Wall", "victim-repo", "main"), CancellationToken.None);
            string deep = Path.Combine(workspace.Folder, relative.Replace('/', Path.DirectorySeparatorChar));
            bool cloned = File.Exists(deep);
            await workspace.DisposeAsync();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(deep.Length, Is.GreaterThan(260), "the test path is not long enough to prove anything");
                Assert.That(cloned, Is.True);
                Assert.That(Directory.Exists(workspace.Folder), Is.False);
            }
        }

        // Decision 16: ADR-016 asks for the free disk space before and after, since the replica's disk is about 2 GiB
        [Test]
        public async Task OpenAsync_LogsTheCloneWithFreeSpace()
        {
            CreateVictimRepo();

            await using (RepositoryWorkspace workspace = await Source(BareRepository.OrganizationUrl(_repos.FullPath))
                .OpenAsync(Request("Kudos Wall", "victim-repo", "main"), CancellationToken.None))
            {
                Assert.That(_logger.Collector.GetSnapshot().Select(record => record.Message),
                    Has.One.StartsWith("Cloned Kudos Wall/victim-repo main at ").And.Contain(" MB free on "));
            }
        }

        // #38: the commit the clone got, which RepositoryScanRunner saves as ScanRun.CommitSha
        [Test]
        public async Task OpenAsync_GivesTheCommitIdOfTheBranchHead()
        {
            string commitId = CreateVictimRepo();

            await using (RepositoryWorkspace workspace = await Source(BareRepository.OrganizationUrl(_repos.FullPath))
                .OpenAsync(Request("Kudos Wall", "victim-repo", "main"), CancellationToken.None))
            {
                Assert.That(workspace.CommitSha, Is.EqualTo(commitId));
            }
        }

        [Test]
        public async Task OpenAsync_LogsTheCommitId()
        {
            string commitId = CreateVictimRepo();

            await using (RepositoryWorkspace workspace = await Source(BareRepository.OrganizationUrl(_repos.FullPath))
                .OpenAsync(Request("Kudos Wall", "victim-repo", "main"), CancellationToken.None))
            {
                Assert.That(_logger.Collector.GetSnapshot().Select(record => record.Message),
                    Has.One.StartsWith("Cloned Kudos Wall/victim-repo main at " + commitId + " in "));
            }
        }

        [Test]
        public void ParseCommitId_GivesTheTrimmedId()
        {
            Assert.That(AzureDevOpsCloneSource.ParseCommitId("27487b917ee49183c0c677159a43707cf20a9219\n"), Is.EqualTo("27487b917ee49183c0c677159a43707cf20a9219"));
        }

        // ScanRun.CommitSha is varchar(40), and Azure DevOps uses SHA-1: anything else means something is badly wrong
        [TestCase("")]
        [TestCase("fatal: not a git repository")]
        // SHA-256, which does not fit varchar(40)
        [TestCase("6f1ed002ab5595859014ebf0951522d9e1b1f6b3c3f7a4c3c1f1e0c8f1b2a3d4")]
        public void ParseCommitId_ForAnythingButFortyHexCharacters_Throws(string output)
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => AzureDevOpsCloneSource.ParseCommitId(output));

            Assert.That(exception.Message, Is.EqualTo("git rev-parse HEAD gave an unexpected commit ID"));
        }

        // Checked on 2026-10-01 against Azure DevOps with a fake token: the first is what git printed
        [TestCase("fatal: could not read Username for 'https://dev.azure.com/BlacklightInternalsMo/Kudos%20Wall/_git/victim-repo': terminal prompts disabled", true)]
        [TestCase("fatal: Authentication failed for 'https://dev.azure.com/BlacklightInternalsMo/Kudos%20Wall/_git/victim-repo/'", true)]
        [TestCase("fatal: unable to access 'https://dev.azure.com/x/': The requested URL returned error: 401", true)]
        [TestCase("fatal: unable to access 'https://dev.azure.com/x/': The requested URL returned error: 403", true)]
        [TestCase("remote: TF401019: The Git repository with name or identifier ghost does not exist or you do not have permissions for the operation you are attempting.", false)]
        [TestCase("warning: Could not find remote branch release to clone.", false)]
        public void IsRejectedToken_RecognisesOnlyARefusedToken(string standardError, bool expected)
        {
            Assert.That(AzureDevOpsCloneSource.IsRejectedToken(standardError), Is.EqualTo(expected));
        }

        // victim-repo in Kudos Wall, as in BlacklightInternalsMo: a README.md on main, after a first commit. Returns the
        // commit ID a clone of main gets.
        private string CreateVictimRepo()
        {
            string content = _temp.WriteText("content/README.md", "second commit");
            return BareRepository.Create(_repos.FullPath, "Kudos Wall", "victim-repo", "main", Path.GetDirectoryName(content)!);
        }

        private AzureDevOpsCloneSource Source(string organizationUrl)
        {
            return new AzureDevOpsCloneSource(
                Options.Create(new AzureDevOpsOptions { OrganizationUrl = organizationUrl, Pat = Pat }),
                new GitProcess(_time, new FakeLogger<GitProcess>()),
                _folders,
                _logger);
        }

        private static ScanRequest Request(string projectName, string repositoryName, string branch)
        {
            return new ScanRequest(Guid.NewGuid(), Guid.NewGuid(), projectName, repositoryName, branch);
        }
    }
}
