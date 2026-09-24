using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Serilog.Extensions.Logging;

namespace RepoScanner.Scanner.Tests
{
    [TestFixture]
    public class ProgramTests
    {
        // Runs the real host end to end. Exit codes for failures (1) and invalid config (2) arrive in task 5.
        [Test]
        public async Task Main_WithNoArguments_ReturnsZero()
        {
            int exitCode = await Program.Main(Array.Empty<string>());

            Assert.That(exitCode, Is.EqualTo(0));
        }

        [Test]
        public void CreateBuilder_WhenBuilt_UsesSerilogForLogging()
        {
            HostApplicationBuilder builder = Program.CreateBuilder(Array.Empty<string>());

            using (IHost host = builder.Build())
            {
                ILoggerFactory loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();

                Assert.That(loggerFactory, Is.TypeOf<SerilogLoggerFactory>());
            }
        }
    }
}
