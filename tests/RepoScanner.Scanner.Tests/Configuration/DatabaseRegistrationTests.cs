using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using RepoScanner.Core.Persistence;
using RepoScanner.Data;
using RepoScanner.Data.Repositories;

namespace RepoScanner.Scanner.Tests.Configuration
{
    [TestFixture]
    public class DatabaseRegistrationTests
    {
        private const string VariableName = "Scanner__Database__ConnectionString";

        private static readonly string[] QueueArgs =
        {
            "--Scanner:Queue:Name", "scan-nightly",
            "--Scanner:Queue:ConnectionString", "UseDevelopmentStorage=true",
            "--Scanner:AzureDevOps:OrganizationUrl", "https://dev.azure.com/example",
            // A fake PAT: values starting "your-" are placeholders to RepoScanner's own self-scan
            "--Scanner:AzureDevOps:Pat", "your-test-pat-0001"
        };

        // The developer's own variable (README: Database and migrations) is removed for each test and
        // put back afterwards, so "missing" really is missing on their machine too
        private string? _originalValue;

        [SetUp]
        public void SetUp()
        {
            _originalValue = Environment.GetEnvironmentVariable(VariableName);
            Environment.SetEnvironmentVariable(VariableName, null);
        }

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable(VariableName, _originalValue);
        }

        // #71 acceptance criterion: a missing connection string stops the scanner with exit code 2 and
        // one log line naming the setting
        [Test]
        public async Task Main_WithoutDatabaseConnectionString_ExitsTwoWithOneLineNamingTheSetting()
        {
            int exitCode = 0;
            string output = await ConsoleCapture.RunAsync(async () => { exitCode = await Program.Main(QueueArgs); });

            using (Assert.EnterMultipleScope())
            {
                Assert.That(exitCode, Is.EqualTo(2));
                Assert.That(output, Does.Contain("Scanner:Database:ConnectionString"));
                Assert.That(LogLines.Read(output).Count(line => LogLines.Level(line) == "Fatal"), Is.EqualTo(1));
                Assert.That(output, Does.Not.Contain("   at "), "A configuration error should not print a stack trace");
            }
        }

        // #71 acceptance criterion: the DbContext and both repositories resolve from the host's services,
        // and the context retries transient SQL errors (ADR-015). Resolving does not connect.
        [Test]
        public void CreateBuilder_ResolvesTheDbContextWithRetryAndBothRepositories()
        {
            string[] args = QueueArgs.Concat(new string[] { "--Scanner:Database:ConnectionString", "Server=unused;Database=unused" }).ToArray();

            using (IHost host = Program.CreateBuilder(args).Build())
            using (IServiceScope scope = host.Services.CreateScope())
            {
                ScannerDbContext context = scope.ServiceProvider.GetRequiredService<ScannerDbContext>();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(context.Database.ProviderName, Is.EqualTo("Microsoft.EntityFrameworkCore.SqlServer"));
                    Assert.That(context.Database.CreateExecutionStrategy(), Is.TypeOf<SqlServerRetryingExecutionStrategy>());
                    Assert.That(scope.ServiceProvider.GetRequiredService<IScanRunRepository>(), Is.TypeOf<ScanRunRepository>());
                    Assert.That(scope.ServiceProvider.GetRequiredService<IFindingRepository>(), Is.TypeOf<FindingRepository>());
                }
            }
        }

        // #71 acceptance criterion and ADR-007: no appsettings file holds a connection string
        [Test]
        public void AppSettingsFiles_HaveNoDatabaseSection()
        {
            string[] files = Directory.GetFiles(Path.Combine(RepositoryRoot.FullPath, "src", "RepoScanner.Scanner"), "appsettings*.json");
            Assert.That(files, Is.Not.Empty);

            foreach (string file in files)
            {
                IConfiguration configuration = new ConfigurationBuilder().AddJsonFile(file).Build();

                Assert.That(configuration.GetSection("Scanner:Database").Exists(), Is.False, Path.GetFileName(file));
            }
        }
    }
}
