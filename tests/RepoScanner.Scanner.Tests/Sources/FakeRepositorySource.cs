using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RepoScanner.Scanner.Queue;
using RepoScanner.Scanner.Sources;

namespace RepoScanner.Scanner.Tests.Sources
{
    // A queued repository's code as a folder named after it under root, for the loop's tests, which are about the loop
    // and not about cloning. Failure makes every open fail, such as with a rejected PAT.
    internal sealed class FakeRepositorySource : IRepositorySource
    {
        private readonly string _root;

        public FakeRepositorySource(string root)
        {
            _root = root;
        }

        public Exception? Failure { get; set; }

        public Task<RepositoryWorkspace> OpenAsync(ScanRequest request, CancellationToken cancellationToken)
        {
            if (Failure != null)
            {
                return Task.FromException<RepositoryWorkspace>(Failure);
            }

            // A missing folder stands in for a clone that fails: the scan run is Failed and the message is retried
            string folder = Path.Combine(_root, request.RepositoryName);
            if (!Directory.Exists(folder))
            {
                return Task.FromException<RepositoryWorkspace>(new DirectoryNotFoundException("No folder named " + request.RepositoryName + " in " + _root));
            }

            return Task.FromResult(RepositoryWorkspace.ForExistingFolder(folder));
        }
    }
}
