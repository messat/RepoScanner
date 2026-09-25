using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace RepoScanner.Data
{
    // Used only by the dotnet ef tool (migrations add, database update, migrations script), so it can build
    // the context without starting the scanner. The connection string comes from an environment variable
    // and is never written in code (ADR-007): locally a user environment variable pointing at .\SQLEXPRESS
    // with Windows authentication, in the pipeline a variable using Entra authentication (task #73).
    public sealed class ScannerDbContextFactory : IDesignTimeDbContextFactory<ScannerDbContext>
    {
        // The Scanner:Database:ConnectionString setting as an environment variable (":" becomes "__"),
        // the same setting the scanner reads at run time from task #71
        private const string ConnectionStringVariable = "Scanner__Database__ConnectionString";

        public ScannerDbContext CreateDbContext(string[] args)
        {
            string? connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);

            // Say exactly what to set, rather than fail later with a vague connection error
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    $"The environment variable {ConnectionStringVariable} is not set. Set it to the database connection string " +
                    "before running dotnet ef: see \"Database and migrations\" in the README.");
            }

            DbContextOptions<ScannerDbContext> options = new DbContextOptionsBuilder<ScannerDbContext>()
                .UseSqlServer(connectionString)
                .Options;

            return new ScannerDbContext(options);
        }
    }
}
