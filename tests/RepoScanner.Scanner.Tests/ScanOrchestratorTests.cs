using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using RepoScanner.Scanner.Configuration;

namespace RepoScanner.Scanner.Tests
{
    [TestFixture]
    public class ScanOrchestratorTests
    {
        private FakeLogger<ScanOrchestrator> _logger = null!;
        private ScanOrchestrator _orchestrator = null!;

        [SetUp]
        public void SetUp()
        {
            _logger = new FakeLogger<ScanOrchestrator>();
            HostingEnvironment environment = new HostingEnvironment { EnvironmentName = "Test" };
            IOptions<QueueOptions> queueOptions = Options.Create(new QueueOptions { Name = "scan-test", ConnectionString = "UseDevelopmentStorage=true" });
            _orchestrator = new ScanOrchestrator(_logger, environment, queueOptions);
        }

        [Test]
        public async Task RunAsync_WhenStarting_LogsEnvironmentAsStructuredProperty()
        {
            await _orchestrator.RunAsync(CancellationToken.None);

            IReadOnlyList<FakeLogRecord> records = _logger.Collector.GetSnapshot();
            Assert.That(records[0].GetStructuredStateValue("EnvironmentName"), Is.EqualTo("Test"));
        }

        [Test]
        public async Task RunAsync_WhenStarting_LogsQueueNameAsStructuredProperty()
        {
            await _orchestrator.RunAsync(CancellationToken.None);

            IReadOnlyList<FakeLogRecord> records = _logger.Collector.GetSnapshot();
            Assert.That(records[0].GetStructuredStateValue("QueueName"), Is.EqualTo("scan-test"));
        }

        [Test]
        public async Task RunAsync_WhenThereIsNoWork_LogsNoWorkToDo()
        {
            await _orchestrator.RunAsync(CancellationToken.None);

            Assert.That(_logger.LatestRecord.Message, Is.EqualTo("No work to do, exiting"));
        }
    }
}
