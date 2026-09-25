using System;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace RepoScanner.Data.Tests
{
    [TestFixture]
    public class ScannerDbContextFactoryTests
    {
        private const string VariableName = "Scanner__Database__ConnectionString";

        // The developer's own value, if set, is put back after each test
        private string? _originalValue;

        [SetUp]
        public void SetUp()
        {
            _originalValue = Environment.GetEnvironmentVariable(VariableName);
        }

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable(VariableName, _originalValue);
        }

        // dotnet ef must say which variable to set, not fail later with a vague connection error
        [TestCase(null)]
        [TestCase("   ")]
        public void CreateDbContext_WithoutTheConnectionStringVariable_ThrowsNamingTheVariable(string? value)
        {
            Environment.SetEnvironmentVariable(VariableName, value);
            ScannerDbContextFactory factory = new ScannerDbContextFactory();

            Assert.That(() => factory.CreateDbContext(Array.Empty<string>()),
                Throws.InvalidOperationException.With.Message.Contains(VariableName));
        }

        [Test]
        public void CreateDbContext_WithTheConnectionStringVariable_UsesSqlServerWithThatValue()
        {
            const string connectionString = "Server=test-server;Database=TestDatabase;Trusted_Connection=True";
            Environment.SetEnvironmentVariable(VariableName, connectionString);
            ScannerDbContextFactory factory = new ScannerDbContextFactory();

            using (ScannerDbContext context = factory.CreateDbContext(Array.Empty<string>()))
            {
                // The SQL Server provider rewrites the connection string in its own form (Server= becomes
                // Data Source=, and it adds settings), so compare what it points at, not the exact text
                SqlConnectionStringBuilder used = new SqlConnectionStringBuilder(context.Database.GetConnectionString());

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(context.Database.ProviderName, Is.EqualTo("Microsoft.EntityFrameworkCore.SqlServer"));
                    Assert.That(used.DataSource, Is.EqualTo("test-server"));
                    Assert.That(used.InitialCatalog, Is.EqualTo("TestDatabase"));
                }
            }
        }
    }
}
