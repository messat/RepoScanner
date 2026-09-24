using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Testing;
using NUnit.Framework;

namespace RepoScanner.Scanner.Tests
{
    [TestFixture]
    public class ScanOrchestratorTests
    {
        [Test]
        public async Task RunAsync_WhenThereIsNoWork_LogsNoWorkToDo()
        {
            FakeLogger<ScanOrchestrator> logger = new FakeLogger<ScanOrchestrator>();
            ScanOrchestrator orchestrator = new ScanOrchestrator(logger);

            await orchestrator.RunAsync(CancellationToken.None);

            Assert.That(logger.LatestRecord.Message, Is.EqualTo("No work to do, exiting"));
        }
    }
}
