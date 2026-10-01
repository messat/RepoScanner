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

        public RepositoryWorkspace(string folder, Func<ValueTask> cleanUp)
        {
            Folder = folder;
            _cleanUp = cleanUp;
        }

        public string Folder { get; }

        // A folder that stays where it is after the scan: --path, and queue mode's local folders in Issue 7
        public static RepositoryWorkspace ForExistingFolder(string folder)
        {
            return new RepositoryWorkspace(folder, () => ValueTask.CompletedTask);
        }

        public ValueTask DisposeAsync()
        {
            return _cleanUp();
        }
    }
}
