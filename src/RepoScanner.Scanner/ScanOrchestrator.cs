using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RepoScanner.Core.Entities;
using RepoScanner.Core.Enums;
using RepoScanner.Core.Persistence;
using RepoScanner.Scanner.Configuration;
using RepoScanner.Scanner.Logging;
using RepoScanner.Scanner.Queue;
using RepoScanner.Scanner.Sources;

namespace RepoScanner.Scanner
{
    // Runs one execution of the scanner job (#31, Issue 7 design, The loop): takes messages from the lane's queue
    // one at a time and scans each repository, until the queue is empty or the time budget would not fit another
    // repository. A failed scan leaves its message, to be tried again; a malformed message, or one already tried
    // MaxDequeueCount times, goes to the poison queue. A repository another run is scanning is skipped, and a run
    // abandoned by one that died is taken over (#33, ADR-027). A PAT Azure DevOps refuses stops the run (#37).
    public sealed class ScanOrchestrator
    {
        // A Running scan run this much older than ScanTimeoutPerRepoMinutes has been abandoned (ADR-027)
        private const int AbandonedAfterExtraMinutes = 5;

        private readonly ILogger<ScanOrchestrator> _logger;
        private readonly IHostEnvironment _environment;
        private readonly QueueOptions _queueOptions;
        private readonly TimeSpan _budget;
        private readonly TimeSpan _scanTimeout;
        private readonly TimeSpan _visibilityTimeout;
        private readonly TimeSpan _abandonedAfter;
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

            // The 5 minutes cover saving after a scan stops and small clock differences between machines (ADR-027)
            _abandonedAfter = _scanTimeout + TimeSpan.FromMinutes(AbandonedAfterExtraMinutes);
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
            catch (AzureDevOpsAuthenticationException)
            {
                // Every other repository would fail the same way, so the rest of the messages are left for when the PAT
                // is fixed (Issue 8 design, decision 13). The scan run already says why. Program exits with 1.
                LogStopped("Azure DevOps refused the token", counts);
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

            // Every line about this request, from any class, carries its correlation ID and repository until its message
            // is dealt with, so one search finds them among every repository the run scans (#62)
            using (_logger.BeginScope(ScanLogScope.For(request.CorrelationId, request.ProjectName, request.RepositoryName)))
            {
                await HandleRequestAsync(message, request, counts, cancellationToken);
            }
        }

        // A message that was read: skipped, poisoned after too many attempts, or scanned, then deleted once its scan run
        // is saved
        private async Task HandleRequestAsync(ScanQueueMessage message, ScanRequest request, RunCounts counts, CancellationToken cancellationToken)
        {
            // A scope per message, so each repository gets a fresh DbContext
            using (IServiceScope scope = _scopes.CreateScope())
            {
                IScanRunRepository scanRuns = scope.ServiceProvider.GetRequiredService<IScanRunRepository>();

                // Is the repository being scanned right now (ADR-027)? Not caught: with the database out of reach every
                // message would fail the same way, so the run stops (exit 1) and the message is left to reappear.
                IReadOnlyList<ScanRun> running = await scanRuns.GetRunningAsync(request.RepositoryId, cancellationToken);
                DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
                List<ScanRun> abandoned = running.Where(run => now - run.StartedUtc > _abandonedAfter).ToList();

                // Tried MaxDequeueCount times already: the failures that threw are in those attempts' scan runs
                // (ADR-013). A crash left its run Running, so that one is marked Failed here. A younger Running run
                // belongs to another live run and is left alone.
                if (message.DequeueCount > _queueOptions.MaxDequeueCount)
                {
                    await MarkFailedAsync(
                        scanRuns, abandoned, "Moved to " + _queueOptions.PoisonQueueName + " after " + _queueOptions.MaxDequeueCount + " attempts", cancellationToken);
                    await _queue.MoveToPoisonAsync(message, cancellationToken);
                    _logger.LogWarning(
                        "Message {MessageId} moved to {PoisonQueueName}: {Reason}, for {ProjectName}/{RepositoryName}, correlation {CorrelationId}, attempt {DequeueCount}",
                        message.MessageId, _queueOptions.PoisonQueueName, "tried " + _queueOptions.MaxDequeueCount + " times without success",
                        request.ProjectName, request.RepositoryName, request.CorrelationId, message.DequeueCount);
                    counts.Poisoned++;
                    return;
                }

                // Another run is scanning it: its findings will be as fresh as this scan's, so this request is
                // deleted rather than kept (ADR-027)
                ScanRun? live = running.FirstOrDefault(run => now - run.StartedUtc <= _abandonedAfter);
                if (live != null)
                {
                    await _queue.DeleteAsync(message, cancellationToken);
                    _logger.LogInformation(
                        "Skipped {ProjectName}/{RepositoryName} from message {MessageId}: scan run {ScanRunId} is already scanning it",
                        request.ProjectName, request.RepositoryName, message.MessageId, live.Id);
                    counts.Skipped++;
                    return;
                }

                // A live scan is stopped at ScanTimeoutPerRepoMinutes, so a run still Running 5 minutes after that
                // belongs to a run that died: it is marked Failed, and this run scans the repository (ADR-027)
                await MarkFailedAsync(
                    scanRuns,
                    abandoned,
                    "Abandoned: still Running more than " + _abandonedAfter.TotalMinutes.ToString(CultureInfo.InvariantCulture)
                    + " minutes after it started, so the run that started it had stopped",
                    cancellationToken);

                // Said in the log as well as in ScanRuns, so a take-over shows in the console and Log Analytics
                foreach (ScanRun run in abandoned)
                {
                    _logger.LogWarning(
                        "Scan run {ScanRunId} for {ProjectName}/{RepositoryName} was abandoned (still Running {MinutesRunning} minutes after it started); marked Failed and scanning again",
                        run.Id, request.ProjectName, request.RepositoryName, (int)(now - run.StartedUtc).TotalMinutes);
                }

                _logger.LogInformation(
                    "Message {MessageId} for {ProjectName}/{RepositoryName}, correlation {CorrelationId}, attempt {DequeueCount}",
                    message.MessageId, request.ProjectName, request.RepositoryName, request.CorrelationId, message.DequeueCount);

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
                catch (AzureDevOpsAuthenticationException)
                {
                    // Not a failed repository but a failed PAT: the run stops (RunAsync), leaving this message too
                    counts.Failed++;
                    throw;
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

        // Marks scan runs Failed whose run stopped without saving an outcome, so none stays Running for ever (ADR-013)
        private static async Task MarkFailedAsync(IScanRunRepository scanRuns, IReadOnlyList<ScanRun> runs, string reason, CancellationToken cancellationToken)
        {
            foreach (ScanRun run in runs)
            {
                ScanRunCompletion completion = new ScanRunCompletion(
                    ScanRunStatus.Failed, CommitSha: null, DateTime.UtcNow, NewCount: 0, ReopenedCount: 0, ResolvedCount: 0, reason, Array.Empty<ScannerResult>());
                await scanRuns.CompleteRunAsync(run.Id, completion, cancellationToken);
            }
        }

        private void LogStopped(string reason, RunCounts counts)
        {
            _logger.LogInformation(
                "Stopped taking messages from {QueueName}: {Reason}. {ScannedCount} scanned, {FailedCount} failed, {SkippedCount} skipped, {PoisonedCount} poisoned",
                _queueOptions.Name, reason, counts.Scanned, counts.Failed, counts.Skipped, counts.Poisoned);
        }

        // What one execution did, for its last log line
        private sealed class RunCounts
        {
            public int Scanned { get; set; }

            public int Failed { get; set; }

            public int Skipped { get; set; }

            public int Poisoned { get; set; }
        }
    }
}
