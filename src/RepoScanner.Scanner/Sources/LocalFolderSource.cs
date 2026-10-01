using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using RepoScanner.Scanner.Configuration;
using RepoScanner.Scanner.Queue;

namespace RepoScanner.Scanner.Sources
{
    // Issue 7 only: a queued repository's code is the folder named after it directly inside Scanner:Source:LocalRoot,
    // so the loop can be shown with Azurite and no Azure DevOps. Issue 8 replaces it with a clone (#31).
    public sealed class LocalFolderSource : IRepositorySource
    {
        private readonly string _root;

        public LocalFolderSource(IOptions<SourceOptions> options)
        {
            _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Value.LocalRoot));
        }

        public Task<RepositoryWorkspace> OpenAsync(ScanRequest request, CancellationToken cancellationToken)
        {
            string folder = Path.GetFullPath(Path.Combine(_root, request.RepositoryName));

            // ScanRequestReader already refuses \, / and .. in the name. Checked again here, where it matters: the
            // folder must be directly inside LocalRoot, never above or deeper in it.
            if (!string.Equals(Path.GetDirectoryName(folder), _root, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("repositoryName must name a folder directly inside Scanner:Source:LocalRoot.", nameof(request));
            }

            // Recorded as the scan run's error, then retried and poisoned like any failed scan
            if (!Directory.Exists(folder))
            {
                throw new DirectoryNotFoundException("No folder named " + request.RepositoryName + " in " + _root);
            }

            // The developer's own folder: closing the workspace leaves it alone
            return Task.FromResult(RepositoryWorkspace.ForExistingFolder(folder));
        }
    }
}
