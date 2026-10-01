using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RepoScanner.Scanner.Configuration;
using RepoScanner.Scanner.Git;
using RepoScanner.Scanner.Queue;

namespace RepoScanner.Scanner.Sources
{
    // Queue mode's code (#37, Issue 8 design, The clone): a shallow clone of the message's repository and branch into a
    // new folder, which closing the workspace deletes. Singleton: it keeps nothing between clones.
    public sealed class AzureDevOpsCloneSource : IRepositorySource
    {
        private const long BytesPerMegabyte = 1024 * 1024;

        // A SHA-1 commit ID in hexadecimal
        private const int CommitIdLength = 40;

        // What git prints when Azure DevOps refuses the PAT. With prompts and sign-in helpers off it cannot ask for
        // another login, so it says "terminal prompts disabled" (checked against Azure DevOps on 2026-10-01); the others
        // are its other wordings for the same refusal.
        private static readonly string[] RejectedTokenMarkers =
        {
            "terminal prompts disabled", "Authentication failed", "returned error: 401", "returned error: 403"
        };

        private readonly AzureDevOpsOptions _options;
        private readonly GitProcess _git;
        private readonly WorkspaceFolders _folders;
        private readonly ILogger<AzureDevOpsCloneSource> _logger;

        public AzureDevOpsCloneSource(IOptions<AzureDevOpsOptions> options, GitProcess git, WorkspaceFolders folders, ILogger<AzureDevOpsCloneSource> logger)
        {
            _options = options.Value;
            _git = git;
            _folders = folders;
            _logger = logger;
        }

        public async Task<RepositoryWorkspace> OpenAsync(ScanRequest request, CancellationToken cancellationToken)
        {
            string url = CloneUrl.For(_options.OrganizationUrl, request.ProjectName, request.RepositoryName);
            string folder = _folders.Create();

            try
            {
                long? freeBefore = FreeMegabytes(folder);
                Stopwatch duration = Stopwatch.StartNew();

                await CloneAsync(url, request.DefaultBranch, folder, cancellationToken);

                // Which commit was scanned, saved as ScanRun.CommitSha (#38)
                string commitSha = ParseCommitId(await _git.RunAsync(GitCommand.RevParseHead(folder), cancellationToken));

                // ADR-016 asks for the free space before and after: the replica's disk is about 2 GiB
                _logger.LogInformation(
                    "Cloned {ProjectName}/{RepositoryName} {Branch} at {CommitSha} in {DurationMs} ms; {FreeMegabytes} MB free on {Drive}, {FreeMegabytesBefore} before",
                    request.ProjectName, request.RepositoryName, request.DefaultBranch, commitSha, duration.ElapsedMilliseconds,
                    FreeMegabytes(folder), Path.GetPathRoot(folder), freeBefore);

                // Closed by RepositoryScanRunner however the scan ends, which deletes the clone (success criterion 3)
                return new RepositoryWorkspace(folder, commitSha, () =>
                {
                    _folders.Delete(folder);
                    return ValueTask.CompletedTask;
                });
            }
            catch (Exception)
            {
                // No workspace was handed back, so nothing else would delete the folder
                _folders.Delete(folder);
                throw;
            }
        }

        // True when git's error text says Azure DevOps refused the PAT, as opposed to any other failure
        internal static bool IsRejectedToken(string standardError)
        {
            return RejectedTokenMarkers.Any(marker => standardError.Contains(marker, StringComparison.OrdinalIgnoreCase));
        }

        // git rev-parse HEAD's answer: 40 lower-case hexadecimal characters, since Azure DevOps uses SHA-1 and
        // ScanRun.CommitSha is varchar(40). Anything else means something is badly wrong, so the scan does not go on.
        internal static string ParseCommitId(string output)
        {
            string commitId = output.Trim();
            if (commitId.Length != CommitIdLength || !commitId.All(char.IsAsciiHexDigitLower))
            {
                throw new InvalidOperationException("git rev-parse HEAD gave an unexpected commit ID");
            }

            return commitId;
        }

        private async Task CloneAsync(string url, string branch, string folder, CancellationToken cancellationToken)
        {
            try
            {
                await _git.RunAsync(GitCommand.Clone(url, branch, folder, _options.Pat), cancellationToken);
            }
            catch (GitCommandException exception) when (IsRejectedToken(exception.StandardError))
            {
                // Every other repository would fail the same way, so this one stops the run (decision 13)
                throw new AzureDevOpsAuthenticationException(
                    "Azure DevOps refused the token in Scanner:AzureDevOps:Pat for " + _options.OrganizationUrl
                    + ". Check that it has not expired or been revoked, and that it has Code (Read).",
                    exception);
            }
        }

        // Free space on the folder's drive, for the log line only, so a drive that cannot be read never fails the scan
        private static long? FreeMegabytes(string folder)
        {
            try
            {
                return new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace / BytesPerMegabyte;
            }
            catch (Exception exception) when (exception is ArgumentException || exception is IOException || exception is UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
