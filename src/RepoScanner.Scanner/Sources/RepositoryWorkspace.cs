using System;
using System.Threading.Tasks;

namespace RepoScanner.Scanner.Sources
{
    // A folder holding a repository's code for one scan (#31). Closing it cleans up: nothing for a folder that
    // was already on the machine, deleting the clone from Issue 8. RepositoryScanRunner always closes it, even
    // when the scan fails.
    public sealed class RepositoryWorkspace : IAsyncDisposable
    {
        private readonly Func<ValueTask> _cleanUp;

        public RepositoryWorkspace(string folder, string? commitSha, Func<ValueTask> cleanUp)
        {
            Folder = folder;
            CommitSha = commitSha;
            _cleanUp = cleanUp;
        }

        public string Folder { get; }

        // The commit the clone got, saved as ScanRun.CommitSha (#38); null for a folder that was not cloned
        public string? CommitSha { get; }

        // A folder that stays where it is after the scan, with no commit ID: --path
        public static RepositoryWorkspace ForExistingFolder(string folder)
        {
            return new RepositoryWorkspace(folder, commitSha: null, () => ValueTask.CompletedTask);
        }

        public ValueTask DisposeAsync()
        {
            return _cleanUp();
        }
    }
}
