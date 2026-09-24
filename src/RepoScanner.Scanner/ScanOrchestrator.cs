using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RepoScanner.Scanner
{
    // Runs one execution of the scanner job: dequeue, clone, scan, reconcile, repeat until
    // there is no work. The queue loop arrives in Issue 5; until then there is never any work.
    public sealed class ScanOrchestrator
    {
        private readonly ILogger<ScanOrchestrator> _logger;
        private readonly IHostEnvironment _environment;

        public ScanOrchestrator(ILogger<ScanOrchestrator> logger, IHostEnvironment environment)
        {
            _logger = logger;
            _environment = environment;
        }

        public Task RunAsync(CancellationToken cancellationToken)
        {
            // {EnvironmentName} is a named property, not string formatting: the value is kept
            // as a separate field on the log event so it can be queried later
            _logger.LogInformation("Scanner starting in {EnvironmentName}", _environment.EnvironmentName);
            _logger.LogInformation("No work to do, exiting");

            return Task.CompletedTask;
        }
    }
}
