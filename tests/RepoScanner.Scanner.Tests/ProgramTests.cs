using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Serilog.Extensions.Logging;

namespace RepoScanner.Scanner.Tests
{
    [TestFixture]
    public class ProgramTests
    {
        // Valid queue settings passed as command-line arguments, the last configuration source to be read
        private static readonly string[] ValidArgs =
        {
            "--Scanner:Queue:Name", "scan-test",
            "--Scanner:Queue:ConnectionString", "UseDevelopmentStorage=true"
        };

        // Runs the real host end to end. Exit codes for failures (1) and invalid config (2) arrive in task 5.
        [Test]
        public async Task Main_WithValidQueueSettings_ReturnsZero()
        {
            int exitCode = await Program.Main(ValidArgs);

            Assert.That(exitCode, Is.EqualTo(0));
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
            string[] args = { "--Scanner:Queue:ConnectionString", "UseDevelopmentStorage=true" };
            HostApplicationBuilder builder = Program.CreateBuilder(args);

            using (IHost host = builder.Build())
            {
                OptionsValidationException? exception = Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

                Assert.That(exception?.Message, Does.Contain("Name"));
            }
        }
    }
}
