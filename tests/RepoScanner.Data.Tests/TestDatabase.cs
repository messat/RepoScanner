using System;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace RepoScanner.Data.Tests
{
    // A new, empty SQLite database in memory for one test. An in-memory database exists only while
    // its connection is open, so the connection stays open and every DbContext the test creates shares it.
    internal sealed class TestDatabase : IDisposable
    {
        private readonly SqliteConnection _connection;

        public TestDatabase()
        {
            // SQLite only enforces foreign keys when asked to, and the delete rules depend on them
            _connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
            _connection.Open();

            using (ScannerDbContext context = CreateContext())
            {
                // Builds the tables from the EF Core model. Only test projects may do this: the scanner
                // never creates or changes the schema (ADR-006, task #13)
                context.Database.EnsureCreated();
            }
        }

        // A new context each time, so a test that reads back what it saved gets it from the database,
        // not from the saving context's change tracker
        public ScannerDbContext CreateContext()
        {
            DbContextOptions<ScannerDbContext> options = new DbContextOptionsBuilder<ScannerDbContext>()
                .UseSqlite(_connection)
                .Options;

            return new ScannerDbContext(options);
        }

        // Reads one value with plain SQL, to see what is actually stored rather than what EF Core converts it back to
        public object? ExecuteScalar(string sql)
        {
            using (SqliteCommand command = _connection.CreateCommand())
            {
                command.CommandText = sql;
                return command.ExecuteScalar();
            }
        }

        public void Dispose()
        {
            _connection.Dispose();
        }
    }
}
