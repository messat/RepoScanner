using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RepoScanner.Core.Entities;
using RepoScanner.Core.Persistence;

namespace RepoScanner.Data.Repositories
{
    // Reads and saves a repository's findings for the reconciler. SaveChangesAsync ends in one
    // SaveChanges, which EF Core runs in one transaction, so a scan's changes are kept whole or not at
    // all (ADR-013). Only SELECT, INSERT and UPDATE are used: all within db_datareader and db_datawriter
    // (ADR-008). Nothing is ever deleted (ADR-019).
    public sealed class FindingRepository : IFindingRepository
    {
        private readonly ScannerDbContext _context;

        public FindingRepository(ScannerDbContext context)
        {
            _context = context;
        }

        // The findings are tracked by this context, so when the reconciler changes them EF Core knows
        // exactly which columns changed
        public async Task<IReadOnlyList<Finding>> GetByRepositoryAsync(int repositoryId, CancellationToken cancellationToken)
        {
            return await _context.Findings
                .Where(finding => finding.RepositoryId == repositoryId)
                .ToListAsync(cancellationToken);
        }

        public async Task SaveChangesAsync(FindingChanges changes, CancellationToken cancellationToken)
        {
            // Their Events (such as Detected) are added with them
            _context.Findings.AddRange(changes.NewFindings);

            foreach (Finding finding in changes.UpdatedFindings)
            {
                // A finding loaded through GetByRepositoryAsync is already tracked, and EF Core writes only
                // the columns the reconciler changed, so a suppression made in the dashboard meanwhile is
                // kept. Update() would write every column. Only a finding this context has never seen
                // needs Update(), or its changes would be silently ignored.
                if (_context.Entry(finding).State == EntityState.Detached)
                {
                    _context.Findings.Update(finding);
                }
            }

            _context.FindingEvents.AddRange(changes.NewEvents);

            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch
            {
                // A failed save leaves its changes pending in this context, so the next SaveChanges (the
                // scanner marking the scan run Failed, ADR-013) would try them again and fail the same way.
                // Discarding them keeps the context usable; the findings are found again by the next scan.
                _context.ChangeTracker.Clear();
                throw;
            }
        }
    }
}
