using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using RepoScanner.Core.Scanning;
using RepoScanner.Scanner.CodePatterns;
using RepoScanner.Scanner.Configuration;
using RepoScanner.Scanner.Local;
using RepoScanner.Scanner.Queue;
using RepoScanner.Scanner.Secrets;
using RepoScanner.Scanner.Sources;
using RepoScanner.Scanner.Tests.Local;
using RepoScanner.Scanner.Tests.Queue;
using RepoScanner.Scanner.Tests.Sources;
using Serilog.Extensions.Logging;

namespace RepoScanner.Scanner.Tests
{
    [TestFixture]
    public class ProgramTests
    {
        // Valid settings passed as command-line arguments, the last configuration source to be read. The tests that
        // run the loop give it an empty queue in memory, so nothing connects to the database or to Azurite, and the
        // connection string only has to be present.
        private static readonly string[] ValidArgs =
        {
            "--Scanner:Queue:Name", "scan-nightly",
            "--Scanner:Queue:ConnectionString", "UseDevelopmentStorage=true",
            "--Scanner:Database:ConnectionString", "Server=unused;Database=unused",
            "--Scanner:AzureDevOps:OrganizationUrl", "https://dev.azure.com/example",
            // A fake PAT: values starting "your-" are placeholders to RepoScanner's own self-scan
            "--Scanner:AzureDevOps:Pat", "your-test-pat-0001"
        };

        // Settings with the required queue Name missing, and only that: every other group is valid, so these tests do not
        // depend on the developer's own environment variables
        private static readonly string[] ArgsWithoutQueueName =
        {
            "--Scanner:Queue:ConnectionString", "UseDevelopmentStorage=true",
            "--Scanner:Database:ConnectionString", "Server=unused;Database=unused",
            "--Scanner:AzureDevOps:OrganizationUrl", "https://dev.azure.com/example",
            "--Scanner:AzureDevOps:Pat", "your-test-pat-0001"
        };

        [Test]
        public async Task RunAsync_WithValidQueueSettingsAndAnEmptyQueue_ReturnsZero()
        {
            int exitCode = await Program.RunAsync(ValidArgs, services => services.AddSingleton<IScanQueue>(new FakeScanQueue()));

            Assert.That(exitCode, Is.EqualTo(0));
        }

        // Issue 7 design, decision 18: a missing queue is a settings problem, found on first use: exit 2 and one line
        [Test]
        public async Task RunAsync_WhenTheQueueDoesNotExist_ExitsTwoWithOneLineNamingIt()
        {
            int exitCode = 0;
            string output = await ConsoleCapture.RunAsync(async () =>
            {
                exitCode = await Program.RunAsync(ValidArgs, services => services.AddSingleton<IScanQueue>(new MissingQueue()));
            });

            using (Assert.EnterMultipleScope())
            {
                Assert.That(exitCode, Is.EqualTo(2));
                Assert.That(output, Does.Contain("The queue scan-nightly does not exist"));
                Assert.That(output.Split('\n').Count(line => line.Contains("FTL]", StringComparison.Ordinal)), Is.EqualTo(1));
                Assert.That(output, Does.Not.Contain("   at "), "A missing queue should not print a stack trace");
            }
        }

        // With queue, execution, Azure DevOps and database settings now, several can be wrong at once on a new machine.
        // Each group is checked on its own, so all of them are listed, still in one line with exit 2.
        [Test]
        public async Task Main_WithSeveralSettingsMissing_ExitsTwoListingEachInOneLine()
        {
            string? database = Environment.GetEnvironmentVariable("Scanner__Database__ConnectionString");
            string? pat = Environment.GetEnvironmentVariable("Scanner__AzureDevOps__Pat");
            Environment.SetEnvironmentVariable("Scanner__Database__ConnectionString", null);
            Environment.SetEnvironmentVariable("Scanner__AzureDevOps__Pat", null);
            try
            {
                string[] args = { "--Scanner:Queue:Name", "scan-nightly", "--Scanner:Queue:ConnectionString", "UseDevelopmentStorage=true" };
                int exitCode = 0;
                string output = await ConsoleCapture.RunAsync(async () => { exitCode = await Program.Main(args); });

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(exitCode, Is.EqualTo(2));
                    Assert.That(output, Does.Contain("Scanner:Database:ConnectionString").And.Contain("Scanner:AzureDevOps:Pat"));
                    Assert.That(output.Split('\n').Count(line => line.Contains("FTL]", StringComparison.Ordinal)), Is.EqualTo(1));
                    Assert.That(output, Does.Not.Contain("   at "), "A configuration error should not print a stack trace");
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("Scanner__Database__ConnectionString", database);
                Environment.SetEnvironmentVariable("Scanner__AzureDevOps__Pat", pat);
            }
        }

        // Queue mode's services, as #31 and #37 wire them. Building the queue clients makes no network call.
        [Test]
        public void CreateBuilder_InQueueMode_RegistersTheLoopTheQueueAndTheClone()
        {
            using (IHost host = Program.CreateBuilder(ValidArgs).Build())
            using (Assert.EnterMultipleScope())
            {
                Assert.That(host.Services.GetService<ScanOrchestrator>(), Is.Not.Null);
                Assert.That(host.Services.GetRequiredService<IScanQueue>(), Is.TypeOf<QueueConsumer>());
                Assert.That(host.Services.GetRequiredService<IRepositorySource>(), Is.TypeOf<AzureDevOpsCloneSource>());
            }
        }

        // Issue 8 design, decision 13: a rejected PAT stops the run with exit 1, like any run that cannot go on, with one
        // line saying so and no stack trace
        [Test]
        public async Task RunAsync_WhenAzureDevOpsRefusesTheToken_ExitsOneWithOneLine()
        {
            using (SqliteTestDatabase database = new SqliteTestDatabase())
            using (TempFolder root = new TempFolder())
            {
                FakeScanQueue queue = new FakeScanQueue();
                queue.Add("{\"schemaVersion\": 1, \"correlationId\": \"" + Guid.NewGuid() + "\", \"repositoryId\": \"" + Guid.NewGuid()
                    + "\", \"projectName\": \"Kudos Wall\", \"repositoryName\": \"victim-repo\", \"defaultBranch\": \"main\"}");
                FakeRepositorySource source = new FakeRepositorySource(root.FullPath)
                {
                    Failure = new AzureDevOpsAuthenticationException(
                        "Azure DevOps refused the token in Scanner:AzureDevOps:Pat for https://dev.azure.com/example. Check that it has not expired or been revoked, and that it has Code (Read).",
                        new InvalidOperationException("git clone failed"))
                };

                int exitCode = 0;
                string output = await ConsoleCapture.RunAsync(async () =>
                {
                    exitCode = await Program.RunAsync(ValidArgs, services =>
                    {
                        database.ReplaceDatabaseIn(services);
                        services.AddSingleton<IScanQueue>(queue);
                        services.AddSingleton<IRepositorySource>(source);
                    });
                });

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(exitCode, Is.EqualTo(1));
                    Assert.That(output, Does.Contain("Azure DevOps refused the token, scanner stopped: Azure DevOps refused the token in Scanner:AzureDevOps:Pat"));
                    Assert.That(output.Split('\n').Count(line => line.Contains("FTL]", StringComparison.Ordinal)), Is.EqualTo(1));
                    Assert.That(output, Does.Not.Contain("   at "), "A rejected PAT should not print a stack trace");
                }
            }
        }

        // Issue 7 design, Settings: 1 minute in Development, so a retry can be watched locally; 65 everywhere else
        [TestCase("Development", 1)]
        [TestCase("Production", 65)]
        public void CreateBuilder_VisibilityTimeout_IsOneMinuteOnlyInDevelopment(string environment, int expectedMinutes)
        {
            string[] args = ValidArgs.Concat(new[] { "--environment", environment }).ToArray();

            using (IHost host = Program.CreateBuilder(args).Build())
            {
                ExecutionOptions options = host.Services.GetRequiredService<IOptions<ExecutionOptions>>().Value;

                Assert.That(options.VisibilityTimeoutMinutes, Is.EqualTo(expectedMinutes));
            }
        }

        // Issue 1 acceptance criterion: a missing Queue:Name exits 2
        [Test]
        public async Task Main_WithoutQueueName_ReturnsTwo()
        {
            int exitCode = await Program.Main(ArgsWithoutQueueName);

            Assert.That(exitCode, Is.EqualTo(2));
        }

        [Test]
        public async Task Main_WithoutQueueName_LogsClearMessageWithoutStackTrace()
        {
            string output = await ConsoleCapture.RunAsync(() => Program.Main(ArgsWithoutQueueName));

            Assert.That(output, Does.Contain("Invalid configuration"));
            Assert.That(output, Does.Contain("The Name field is required."));
            Assert.That(output, Does.Not.Contain("   at "), "A configuration error should not print a stack trace");
        }

        [Test]
        public async Task RunAsync_WhenTheRunFails_ReturnsOne()
        {
            int exitCode = await Program.RunAsync(ValidArgs, services => services.AddHostedService<FailingService>());

            Assert.That(exitCode, Is.EqualTo(1));
        }

        [Test]
        public async Task RunAsync_WhenTheRunFails_LogsTheError()
        {
            string output = await ConsoleCapture.RunAsync(() => Program.RunAsync(ValidArgs, services => services.AddHostedService<FailingService>()));

            Assert.That(output, Does.Contain("Scanner failed"));
            Assert.That(output, Does.Contain("Simulated failure"));
        }

        [Test]
        public void CreateBuilder_WhenBuilt_UsesSerilogForLogging()
        {
            HostApplicationBuilder builder = Program.CreateBuilder(ValidArgs);

            using (IHost host = builder.Build())
            {
                ILoggerFactory loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();

                Assert.That(loggerFactory, Is.TypeOf<SerilogLoggerFactory>());
            }
        }

        [Test]
        public void StartAsync_WithoutQueueName_ThrowsOptionsValidationException()
        {
            HostApplicationBuilder builder = Program.CreateBuilder(ArgsWithoutQueueName);

            using (IHost host = builder.Build())
            {
                OptionsValidationException? exception = Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

                Assert.That(exception?.Message, Does.Contain("Name"));
            }
        }

        [Test]
        public void CreateBuilder_RegistersTheSecretScannerWithAllSevenRules()
        {
            using (IHost host = Program.CreateBuilder(ValidArgs).Build())
            {
                IEnumerable<string> ruleIds = host.Services.GetServices<ISecretRule>().Select(rule => rule.RuleId);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(ruleIds, Is.EquivalentTo(new string[] { "SEC001", "SEC002", "SEC003", "SEC004", "SEC005", "SEC006", "SEC007" }));
                    Assert.That(host.Services.GetServices<IScanner>().OfType<SecretScanner>(), Has.Exactly(1).Items);
                }
            }
        }

        // The code-pattern rules, one more with each of #50 to #53
        [Test]
        public void CreateBuilder_RegistersTheCodePatternRules()
        {
            using (IHost host = Program.CreateBuilder(ValidArgs).Build())
            {
                IEnumerable<string> ruleIds = host.Services.GetServices<ICodePatternRule>().Select(rule => rule.RuleId);

                Assert.That(ruleIds, Is.EquivalentTo(new string[] { "CP001", "CP002", "CP003", "CP004", "CP005", "CP006" }));
            }
        }

        // Local mode reads no queue, so it starts without queue settings (#20, design decision 10)
        [Test]
        public async Task StartAsync_WithPathAndNoQueueSettings_StartsInLocalMode()
        {
            string[] args = { "--path", AppContext.BaseDirectory, "--Scanner:Database:ConnectionString", "Server=unused;Database=unused" };

            using (IHost host = Program.CreateBuilder(args).Build())
            {
                await host.StartAsync();

                using (IServiceScope scope = host.Services.CreateScope())
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(scope.ServiceProvider.GetService<LocalScanRunner>(), Is.Not.Null);
                    Assert.That(host.Services.GetService<ScanOrchestrator>(), Is.Null);
                }

                await host.StopAsync();
            }
        }

        // A lane queue that Azure says does not exist
        internal sealed class MissingQueue : IScanQueue
        {
            public Task<ScanQueueMessage?> ReceiveAsync(TimeSpan visibilityTimeout, CancellationToken cancellationToken)
            {
                throw new ScanQueueNotFoundException("scan-nightly", new InvalidOperationException("QueueNotFound"));
            }

            public Task DeleteAsync(ScanQueueMessage message, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public Task MoveToPoisonAsync(ScanQueueMessage message, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }
        }

        // A service that throws as soon as the host starts it, to simulate an unexpected failure
        internal sealed class FailingService : IHostedService
        {
            public Task StartAsync(CancellationToken cancellationToken)
            {
                throw new InvalidOperationException("Simulated failure");
            }

            public Task StopAsync(CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }
        }
    }
}
