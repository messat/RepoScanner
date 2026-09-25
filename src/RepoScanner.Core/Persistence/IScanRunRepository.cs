using System;
using System.Threading;
using System.Threading.Tasks;
using RepoScanner.Core.Entities;
using RepoScanner.Core.Enums;

namespace RepoScanner.Core.Persistence
{
    // Saves the start and end of each scan. The contract lives in Core so business logic can use it
    // without knowing about EF Core; the implementation is in RepoScanner.Data.
    public interface IScanRunRepository
    {
        // Adds the repository if it is new or updates its details if they changed (matched on
        // AzureDevOpsId), and adds a Running scan run for it. Both are saved together or not at all.
        Task<ScanRun> StartRunAsync(
            RepositoryDetails repository,
            ScanTrigger trigger,
            Guid correlationId,
            DateTime startedUtc,
            CancellationToken cancellationToken);

        // Records how the run ended and each scanner's result, saved together or not at all
        Task CompleteRunAsync(int scanRunId, ScanRunCompletion completion, CancellationToken cancellationToken);
    }
}
