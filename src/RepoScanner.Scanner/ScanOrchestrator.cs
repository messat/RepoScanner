using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RepoScanner.Scanner.Configuration;

namespace RepoScanner.Scanner
{
    // Runs one execution of the scanner job: dequeue, clone, scan, reconcile, repeat until
    // there is no work. The queue loop arrives in Issue 5; until then there is never any work.
    public sealed class ScanOrchestrator
    {
        private readonly ILogger<ScanOrchestrator> _logger;
        private readonly IHostEnvironment _environment;
        private readonly QueueOptions _queueOptions;

        public ScanOrchestrator(ILogger<ScanOrchestrator> logger, IHostEnvironment environment, IOptions<QueueOptions> queueOptions)
        {
            _logger = logger;
            _environment = environment;
            _queueOptions = queueOptions.Value;
        }

        public Task RunAsync(CancellationToken cancellationToken)
        {
            // {EnvironmentName} and {QueueName} are named properties, not string formatting: the values
            // are kept as separate fields on the log event so they can be queried later
            _logger.LogInformation("Scanner starting in {EnvironmentName} for queue {QueueName}", _environment.EnvironmentName, _queueOptions.Name);
            _logger.LogInformation("No work to do, exiting");

            return Task.CompletedTask;
        }
    }
}
