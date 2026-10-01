using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RepoScanner.Core.Enums;
using RepoScanner.Core.Persistence;
using RepoScanner.Scanner.Sources;

namespace RepoScanner.Scanner.Local
{
    // Scans a folder on this machine (--path, #20 and #26): no queue and no clone. It describes the folder as a
    // repository and hands it to RepositoryScanRunner, which the queue loop uses too (Issue 7 design, #31).
    public sealed class LocalScanRunner
    {
        // Local folders are kept under this project name, so they are easy to tell apart from Azure
        // DevOps repositories in the Repositories table
        public const string LocalProjectName = "Local";

        public const string LocalBranchName = "local";

        private readonly RepositoryScanRunner _runner;

        public LocalScanRunner(RepositoryScanRunner runner)
        {
            _runner = runner;
        }

        // A local folder has no Azure DevOps ID, so it gets one made from its full path: the first
        // 16 bytes of the SHA-256 of the lower-cased path. The same folder always maps to the same
        // Repositories row, however it is written (design decision 11).
        public static RepositoryDetails DescribeFolder(string folder)
        {
            string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(fullPath.ToLowerInvariant()));
            string name = Path.GetFileName(fullPath);

            // A drive root such as C:\ has no folder name, so its path is used instead
            return new RepositoryDetails(new Guid(hash.AsSpan(0, 16)), LocalProjectName, name.Length > 0 ? name : fullPath, LocalBranchName);
        }

        // On demand, with a new correlation ID and no time limit, as before Issue 7. The folder stays where it is.
        public Task RunAsync(string folder, CancellationToken cancellationToken)
        {
            return _runner.RunAsync(
                DescribeFolder(folder), ScanTrigger.OnDemand, Guid.NewGuid(), _ => Task.FromResult(RepositoryWorkspace.ForExistingFolder(folder)), timeLimit: null, cancellationToken);
        }
    }
}
