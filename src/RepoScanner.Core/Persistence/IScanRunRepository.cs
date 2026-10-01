using System;
using System.Collections.Generic;
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

        // The repository's Running scan runs, oldest first, matched on AzureDevOpsId: empty when it is not being
        // scanned, or not stored yet. The queue loop skips or takes over on these (#33, ADR-027).
        Task<IReadOnlyList<ScanRun>> GetRunningAsync(Guid azureDevOpsId, CancellationToken cancellationToken);
    }
}
