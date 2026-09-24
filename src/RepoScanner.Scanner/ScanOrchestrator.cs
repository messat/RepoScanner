using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RepoScanner.Scanner
{
    // Runs one execution of the scanner job: dequeue, clone, scan, reconcile, repeat until
    // there is no work. The queue loop arrives in Issue 5; until then there is never any work.
    public sealed class ScanOrchestrator
    {
        private readonly ILogger<ScanOrchestrator> _logger;

        public ScanOrchestrator(ILogger<ScanOrchestrator> logger)
        {
            _logger = logger;
        }

        public Task RunAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Scanner starting");
            _logger.LogInformation("No work to do, exiting");

            return Task.CompletedTask;
        }
    }
}
