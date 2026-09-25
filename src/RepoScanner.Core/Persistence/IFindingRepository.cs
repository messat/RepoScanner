using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RepoScanner.Core.Entities;

namespace RepoScanner.Core.Persistence
{
    // Reads and saves a repository's findings for the reconciler. The contract lives in Core so
    // business logic can use it without knowing about EF Core; the implementation is in RepoScanner.Data.
    public interface IFindingRepository
    {
        // Every finding for the repository, whatever its status: resolved findings can be reopened,
        // and suppressed ones must be recognised so they are left alone
        Task<IReadOnlyList<Finding>> GetByRepositoryAsync(int repositoryId, CancellationToken cancellationToken);

        // Adds the new findings, saves the changed ones and adds the new events, together or not at all
        Task SaveChangesAsync(FindingChanges changes, CancellationToken cancellationToken);
    }
}
