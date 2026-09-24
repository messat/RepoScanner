using Microsoft.EntityFrameworkCore;
using RepoScanner.Core.Entities;

namespace RepoScanner.Data
{
    // The scanner's database, shared with the dashboard. It holds no connection string: whoever creates
    // it passes the options in (SQL Server from the design-time factory and, from task #71, the scanner;
    // SQLite in the tests), so the same model works everywhere.
    public sealed class ScannerDbContext : DbContext
    {
        public ScannerDbContext(DbContextOptions<ScannerDbContext> options)
            : base(options)
        {
        }

        public DbSet<Repository> Repositories => Set<Repository>();

        public DbSet<ScanRun> ScanRuns => Set<ScanRun>();

        public DbSet<ScannerResult> ScannerResults => Set<ScannerResult>();

        public DbSet<Finding> Findings => Set<Finding>();

        public DbSet<FindingEvent> FindingEvents => Set<FindingEvent>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Every table's mapping is in its own class in Configurations/, one per entity
            modelBuilder.ApplyConfigurationsFromAssembly(typeof(ScannerDbContext).Assembly);
        }
    }
}
