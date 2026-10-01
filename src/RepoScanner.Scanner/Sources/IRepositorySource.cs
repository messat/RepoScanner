using System.Threading;
using System.Threading.Tasks;
using RepoScanner.Scanner.Queue;

namespace RepoScanner.Scanner.Sources
{
    // Where a queued repository's code comes from (#31): a local folder in Issue 7, a clone from Issue 8. The
    // queue loop does not change when it does.
    public interface IRepositorySource
    {
        Task<RepositoryWorkspace> OpenAsync(ScanRequest request, CancellationToken cancellationToken);
    }
}
