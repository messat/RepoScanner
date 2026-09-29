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
using RepoScanner.Scanner.Local;
using RepoScanner.Scanner.Secrets;
using Serilog.Extensions.Logging;

namespace RepoScanner.Scanner.Tests
{
    [TestFixture]
    public class ProgramTests
    {
        // Valid settings passed as command-line arguments, the last configuration source to be read.
        // Nothing connects to the database in queue mode, so its connection string only has to be present.
        private static readonly string[] ValidArgs =
        {
            "--Scanner:Queue:Name", "scan-test",
            "--Scanner:Queue:ConnectionString", "UseDevelopmentStorage=true",
            "--Scanner:Database:ConnectionString", "Server=unused;Database=unused"
        };

        // Settings with the required queue Name missing
        private static readonly string[] ArgsWithoutQueueName =
        {
            "--Scanner:Queue:ConnectionString", "UseDevelopmentStorage=true",
            "--Scanner:Database:ConnectionString", "Server=unused;Database=unused"
        };

        [Test]
        public async Task Main_WithValidQueueSettings_ReturnsZero()
        {
            int exitCode = await Program.Main(ValidArgs);

            Assert.That(exitCode, Is.EqualTo(0));
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

                Assert.That(ruleIds, Is.EquivalentTo(new string[] { "CP001" }));
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
