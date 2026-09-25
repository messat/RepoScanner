using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RepoScanner.Core.Entities;
using RepoScanner.Core.Enums;
using RepoScanner.Core.Persistence;

namespace RepoScanner.Data.Repositories
{
    // Saves the start and end of each scan. Each method ends in one SaveChanges, which EF Core runs in
    // one transaction, so a write is kept whole or not at all (ADR-013). Only SELECT, INSERT and UPDATE
    // are used: all within db_datareader and db_datawriter (ADR-008).
    public sealed class ScanRunRepository : IScanRunRepository
    {
        private readonly ScannerDbContext _context;

        public ScanRunRepository(ScannerDbContext context)
        {
            _context = context;
        }

        public async Task<ScanRun> StartRunAsync(
            RepositoryDetails repository,
            ScanTrigger trigger,
            Guid correlationId,
            DateTime startedUtc,
            CancellationToken cancellationToken)
        {
            // Matched on Azure DevOps' ID, not the name, so a renamed repository keeps its history
            Repository? stored = await _context.Repositories
                .SingleOrDefaultAsync(existing => existing.AzureDevOpsId == repository.AzureDevOpsId, cancellationToken);

            if (stored == null)
            {
                stored = new Repository { AzureDevOpsId = repository.AzureDevOpsId };
                _context.Repositories.Add(stored);
            }

            // Updated every time; EF Core only writes the columns whose values actually changed
            stored.ProjectName = repository.ProjectName;
            stored.Name = repository.Name;
            stored.DefaultBranch = repository.DefaultBranch;

            ScanRun scanRun = new ScanRun
            {
                Repository = stored,
                TriggerType = trigger,
                Status = ScanRunStatus.Running,
                CorrelationId = correlationId,
                StartedUtc = startedUtc
            };
            _context.ScanRuns.Add(scanRun);

            await _context.SaveChangesAsync(cancellationToken);
            return scanRun;
        }

        public async Task CompleteRunAsync(int scanRunId, ScanRunCompletion completion, CancellationToken cancellationToken)
        {
            ScanRun scanRun = await _context.ScanRuns.SingleAsync(run => run.Id == scanRunId, cancellationToken);

            scanRun.Status = completion.Status;
            scanRun.CommitSha = completion.CommitSha;
            scanRun.CompletedUtc = completion.CompletedUtc;
            scanRun.NewCount = completion.NewCount;
            scanRun.ReopenedCount = completion.ReopenedCount;
            scanRun.ResolvedCount = completion.ResolvedCount;
            scanRun.ErrorMessage = completion.ErrorMessage;

            foreach (ScannerResult result in completion.ScannerResults)
            {
                scanRun.ScannerResults.Add(result);
            }

            await _context.SaveChangesAsync(cancellationToken);
        }
    }
}
