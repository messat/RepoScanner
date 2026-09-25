using System;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RepoScanner.Data;

namespace RepoScanner.Scanner.Tests.Local
{
    // A new, empty SQLite database in memory for one test, as in RepoScanner.Data.Tests. It exists only
    // while its connection is open, so the connection stays open and every context shares it.
    internal sealed class SqliteTestDatabase : IDisposable
    {
        private readonly SqliteConnection _connection;

        public SqliteTestDatabase()
        {
            // SQLite only enforces foreign keys when asked to
            _connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
            _connection.Open();
            Options = new DbContextOptionsBuilder<ScannerDbContext>().UseSqlite(_connection).Options;

            using (ScannerDbContext context = CreateContext())
            {
                // Test projects may build the schema this way; the scanner never does (ADR-006, task #13)
                context.Database.EnsureCreated();
            }
        }

        public DbContextOptions<ScannerDbContext> Options { get; }

        // A new context each time, so a test reads back what was saved from the database itself
        public ScannerDbContext CreateContext()
        {
            return new ScannerDbContext(Options);
        }

        // Swaps the scanner's SQL Server registration for this database, for tests that run the whole
        // program. The context is built from DbContextOptions<ScannerDbContext>, so replacing that one
        // registration is enough.
        public void ReplaceDatabaseIn(IServiceCollection services)
        {
            services.RemoveAll<DbContextOptions<ScannerDbContext>>();
            services.AddScoped<DbContextOptions<ScannerDbContext>>(_ => Options);
        }

        public void Dispose()
        {
            _connection.Dispose();
        }
    }
}
