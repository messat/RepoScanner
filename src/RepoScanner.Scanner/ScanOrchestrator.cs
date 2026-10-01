using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RepoScanner.Core.Enums;
using RepoScanner.Scanner.Configuration;
using RepoScanner.Scanner.Queue;
using RepoScanner.Scanner.Sources;

namespace RepoScanner.Scanner
{
    // Runs one execution of the scanner job (#31, Issue 7 design, The loop): takes messages from the lane's queue
    // one at a time and scans each repository, until the queue is empty or the time budget would not fit another
    // repository. A failed scan leaves its message, to be tried again; a malformed message, or one already tried
    // MaxDequeueCount times, goes to the poison queue.
    public sealed class ScanOrchestrator
    {
        private readonly ILogger<ScanOrchestrator> _logger;
        private readonly IHostEnvironment _environment;
        private readonly QueueOptions _queueOptions;
        private readonly TimeSpan _budget;
        private readonly TimeSpan _scanTimeout;
        private readonly TimeSpan _visibilityTimeout;
        private readonly IScanQueue _queue;
        private readonly IRepositorySource _source;
        private readonly IServiceScopeFactory _scopes;
        private readonly TimeProvider _timeProvider;

        public ScanOrchestrator(
            ILogger<ScanOrchestrator> logger,
            IHostEnvironment environment,
            IOptions<QueueOptions> queueOptions,
            IOptions<ExecutionOptions> executionOptions,
            IScanQueue queue,
            IRepositorySource source,
            IServiceScopeFactory scopes,
            TimeProvider timeProvider)
        {
            _logger = logger;
            _environment = environment;
            _queueOptions = queueOptions.Value;
            _budget = TimeSpan.FromMinutes(executionOptions.Value.MaxRunDurationMinutes);
            _scanTimeout = TimeSpan.FromMinutes(executionOptions.Value.ScanTimeoutPerRepoMinutes);
            _visibilityTimeout = TimeSpan.FromMinutes(executionOptions.Value.VisibilityTimeoutMinutes);
            _queue = queue;
            _source = source;
            _scopes = scopes;
            _timeProvider = timeProvider;
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            // {EnvironmentName} and {QueueName} are named properties, not string formatting: the values
            // are kept as separate fields on the log event so they can be queried later
            _logger.LogInformation("Scanner starting in {EnvironmentName} for queue {QueueName}", _environment.EnvironmentName, _queueOptions.Name);

            RunCounts counts = new RunCounts();
            string reason;
            try
            {
                reason = await TakeMessagesAsync(counts, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Ctrl+C locally, SIGTERM in Azure: the scan that was running is already marked Failed, and its
                // message is left to be tried again. Program exits with 1.
                LogStopped("asked to stop", counts);
                throw;
            }

            LogStopped(reason, counts);
        }

        // Returns why it stopped: the queue was empty, or another repository might not finish in the time left
        private async Task<string> TakeMessagesAsync(RunCounts counts, CancellationToken cancellationToken)
        {
            long started = _timeProvider.GetTimestamp();

            while (true)
            {
                // A repository started now could take ScanTimeoutPerRepoMinutes, and the job is stopped at its
                // replicaTimeout, so none is started unless that much of the budget is left
                if (_budget - _timeProvider.GetElapsedTime(started) < _scanTimeout)
                {
                    return "time budget spent";
                }

                // One message, hidden from other runs while this one works on it (decision 10)
                ScanQueueMessage? message = await _queue.ReceiveAsync(_visibilityTimeout, cancellationToken);
                if (message == null)
                {
                    return "queue empty";
                }

                await HandleAsync(message, counts, cancellationToken);
            }
        }

        private async Task HandleAsync(ScanQueueMessage message, RunCounts counts, CancellationToken cancellationToken)
        {
            // Malformed: retrying cannot help, so straight to poison, with no scan run (decisions 13 and 15)
            if (!ScanRequestReader.TryRead(message.Body, out ScanRequest? request, out string? problem))
            {
                await _queue.MoveToPoisonAsync(message, cancellationToken);
                _logger.LogWarning(
                    "Message {MessageId} moved to {PoisonQueueName}: {Reason}, attempt {DequeueCount}",
                    message.MessageId, _queueOptions.PoisonQueueName, problem, message.DequeueCount);
                counts.Poisoned++;
                return;
            }

            // Tried MaxDequeueCount times already: the failures are in those attempts' scan runs (ADR-013)
            if (message.DequeueCount > _queueOptions.MaxDequeueCount)
            {
                await _queue.MoveToPoisonAsync(message, cancellationToken);
                _logger.LogWarning(
                    "Message {MessageId} moved to {PoisonQueueName}: {Reason}, for {ProjectName}/{RepositoryName}, correlation {CorrelationId}, attempt {DequeueCount}",
                    message.MessageId, _queueOptions.PoisonQueueName, "tried " + _queueOptions.MaxDequeueCount + " times without success",
                    request.ProjectName, request.RepositoryName, request.CorrelationId, message.DequeueCount);
                counts.Poisoned++;
                return;
            }

            _logger.LogInformation(
                "Message {MessageId} for {ProjectName}/{RepositoryName}, correlation {CorrelationId}, attempt {DequeueCount}",
                message.MessageId, request.ProjectName, request.RepositoryName, request.CorrelationId, message.DequeueCount);

            // A scope per message, so each repository gets a fresh DbContext
            using (IServiceScope scope = _scopes.CreateScope())
            {
                RepositoryScanRunner runner = scope.ServiceProvider.GetRequiredService<RepositoryScanRunner>();
                try
                {
                    await runner.RunAsync(
                        request.ToRepositoryDetails(),
                        _queueOptions.Trigger,
                        request.CorrelationId,
                        token => _source.OpenAsync(request, token),
                        _scanTimeout,
                        cancellationToken);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // The run is already marked Failed with the reason. The message is left: it reappears after the
                    // visibility timeout and is tried again, then poisoned (decision 12).
                    _logger.LogWarning(
                        exception,
                        "Scan of {ProjectName}/{RepositoryName} from message {MessageId} failed on attempt {DequeueCount}; the message is left to be tried again",
                        request.ProjectName, request.RepositoryName, message.MessageId, message.DequeueCount);
                    counts.Failed++;
                    return;
                }
            }

            // Only once the scan run is saved (ADR-013)
            await _queue.DeleteAsync(message, cancellationToken);
            counts.Scanned++;
        }

        private void LogStopped(string reason, RunCounts counts)
        {
            _logger.LogInformation(
                "Stopped taking messages from {QueueName}: {Reason}. {ScannedCount} scanned, {FailedCount} failed, {PoisonedCount} poisoned",
                _queueOptions.Name, reason, counts.Scanned, counts.Failed, counts.Poisoned);
        }

        // What one execution did, for its last log line
        private sealed class RunCounts
        {
            public int Scanned { get; set; }

            public int Failed { get; set; }

            public int Poisoned { get; set; }
        }
    }
}
