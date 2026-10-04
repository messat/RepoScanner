using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RepoScanner.Core.Entities;
using RepoScanner.Core.Enums;
using RepoScanner.Core.Persistence;
using RepoScanner.Core.Scanning;
using RepoScanner.Scanner.Logging;
using RepoScanner.Scanner.Reconciliation;
using RepoScanner.Scanner.Sources;

namespace RepoScanner.Scanner
{
    // Scans one repository and saves what it finds (#31): starts a Running scan run, runs every scanner on the
    // folder, reconciles what they found with the findings already stored, and completes the run. --path and the
    // queue loop both use it; only where the repository details and the folder come from differs (Issue 7 design).
    public sealed class RepositoryScanRunner
    {
        private readonly IScanRunRepository _scanRuns;
        private readonly IFindingRepository _findings;
        private readonly IReadOnlyList<IScanner> _scanners;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<RepositoryScanRunner> _logger;

        public RepositoryScanRunner(IScanRunRepository scanRuns, IFindingRepository findings, IEnumerable<IScanner> scanners, TimeProvider timeProvider, ILogger<RepositoryScanRunner> logger)
        {
            _scanRuns = scanRuns;
            _findings = findings;

            // Every registered scanner: only SecretScanner in Issue 3, all three once Issues 5 and 6
            // register theirs. Asking for a single IScanner would silently get the last one registered.
            _scanners = scanners.ToList();
            _timeProvider = timeProvider;
            _logger = logger;
        }

        // Starts a Running scan run, opens the folder, runs every scanner on it, reconciles, and completes the run.
        // A failure after the run started, including opening the folder or going over timeLimit, marks the run
        // Failed and is rethrown. Going over timeLimit is rethrown as a TimeoutException, so the queue loop can tell
        // it from the job being stopped. timeLimit is null for --path.
        public async Task RunAsync(RepositoryDetails repository, ScanTrigger trigger, Guid correlationId,
            Func<CancellationToken, Task<RepositoryWorkspace>> openWorkspace, TimeSpan? timeLimit, CancellationToken cancellationToken)
        {
            // Every line of the scan, from any class, carries its correlation ID and repository (#62). The queue loop has
            // opened the same scope already; for --path this is the only one.
            using (_logger.BeginScope(ScanLogScope.For(correlationId, repository.ProjectName, repository.Name)))
            {
                await ScanAsync(repository, trigger, correlationId, openWorkspace, timeLimit, cancellationToken);
            }
        }

        private async Task ScanAsync(RepositoryDetails repository, ScanTrigger trigger, Guid correlationId,
            Func<CancellationToken, Task<RepositoryWorkspace>> openWorkspace, TimeSpan? timeLimit, CancellationToken cancellationToken)
        {
            ScanRun scanRun = await _scanRuns.StartRunAsync(repository, trigger, correlationId, DateTime.UtcNow, cancellationToken);

            // The time limit counts from here, so opening the folder (from Issue 8, the clone) counts towards it.
            // The scanners and the save are given the combined token: stopped by the job or by the limit.
            using (CancellationTokenSource timeout = new CancellationTokenSource(timeLimit ?? Timeout.InfiniteTimeSpan, _timeProvider))
            using (CancellationTokenSource scan = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token))
            {
                CancellationToken scanToken = scan.Token;
                Stopwatch duration = Stopwatch.StartNew();

                // Set once the run's outcome is saved, so a failure rethrown after that is not recorded a second time
                bool recorded = false;

                // The clone's commit, saved however the run ends once the clone has worked; empty when it failed (#38)
                string? commitSha = null;
                try
                {
                    // Opened after the run started, so a folder that cannot be opened is recorded against it, and always
                    // closed, even when the scan fails (Issue 7 design, decision 8)
                    await using (RepositoryWorkspace workspace = await openWorkspace(scanToken))
                    {
                        commitSha = workspace.CommitSha;
                        string folder = workspace.Folder;
                        _logger.LogInformation("Scanning {Folder} as scan run {ScanRunId}", folder, scanRun.Id);

                        List<RawFinding> found = new List<RawFinding>();
                        List<ScannerResult> results = new List<ScannerResult>();

                        // Only these can resolve their findings: one that failed found nothing because it did not look,
                        // not because everything was fixed (ADR-025, rule 7)
                        HashSet<ScannerType> completedScannerTypes = new HashSet<ScannerType>();
                        Exception? firstFailure = null;

                        foreach (IScanner scanner in _scanners)
                        {
                            // A scanner that does not watch the token must not keep a stopped run going
                            scanToken.ThrowIfCancellationRequested();

                            Stopwatch scannerDuration = Stopwatch.StartNew();
                            IReadOnlyList<RawFinding> scannerFindings;
                            try
                            {
                                scannerFindings = await scanner.ScanAsync(folder, scanToken);
                            }
                            catch (Exception exception) when (!scanToken.IsCancellationRequested)
                            {
                                // One scanner failing, such as the package scanner with nuget.org down, does not stop the
                                // others (#74). A stopped run is not a scanner failing, so that still ends the run below.
                                _logger.LogWarning(exception, "The {ScannerType} scanner failed; the other scanners carry on", scanner.ScannerType);
                                results.Add(new ScannerResult
                                {
                                    ScannerType = scanner.ScannerType,
                                    Status = ScannerStatus.Failed,
                                    FindingCount = 0,
                                    DurationMs = (int)scannerDuration.ElapsedMilliseconds,
                                    ErrorMessage = exception.Message
                                });
                                firstFailure ??= exception;
                                continue;
                            }

                            results.Add(new ScannerResult
                            {
                                ScannerType = scanner.ScannerType,
                                Status = ScannerStatus.Succeeded,
                                FindingCount = scannerFindings.Count,
                                DurationMs = (int)scannerDuration.ElapsedMilliseconds
                            });
                            completedScannerTypes.Add(scanner.ScannerType);
                            found.AddRange(scannerFindings);
                        }

                        // The last scanner may have used up the time: nothing is saved from a run that was stopped
                        scanToken.ThrowIfCancellationRequested();

                        // Which scanners failed and why, for ScanRun.ErrorMessage (ADR-013); null when none failed
                        string? failures = FailureSummary(results);

                        if (firstFailure != null && completedScannerTypes.Count == 0)
                        {
                            // Every scanner failed, so there is nothing to reconcile. The run is Failed with every scanner's
                            // result, and the first failure goes on to the caller (#32).
                            ScanRunCompletion failed = new ScanRunCompletion(
                                ScanRunStatus.Failed, commitSha, DateTime.UtcNow, NewCount: 0, ReopenedCount: 0, ResolvedCount: 0, failures, results);
                            await _scanRuns.CompleteRunAsync(scanRun.Id, failed, scanToken);
                            recorded = true;

                            ExceptionDispatchInfo.Throw(firstFailure);
                        }

                        ReconciliationResult reconciliation = await ReconcileAsync(scanRun, found, completedScannerTypes, scanToken);

                        // Some scanners failed and some succeeded: the findings of those that succeeded are saved, and the
                        // run says the rest are out of date (#32). The process still exits with 0: the run did its job.
                        ScanRunStatus status = failures == null ? ScanRunStatus.Completed : ScanRunStatus.PartiallyCompleted;
                        ScanRunCompletion completion = new ScanRunCompletion(
                            status,
                            commitSha,
                            DateTime.UtcNow,
                            reconciliation.NewCount,
                            reconciliation.ReopenedCount,
                            reconciliation.ResolvedCount,
                            failures,
                            results);
                        await _scanRuns.CompleteRunAsync(scanRun.Id, completion, scanToken);
                        recorded = true;

                        _logger.LogInformation(
                            "Scan run {ScanRunId} {Status} in {DurationMs} ms: {FoundCount} found, {NewCount} new, {ReopenedCount} reopened, {ResolvedCount} resolved, {SuppressionExpiredCount} suppressions expired",
                            scanRun.Id,
                            status,
                            duration.ElapsedMilliseconds,
                            found.Count,
                            reconciliation.NewCount,
                            reconciliation.ReopenedCount,
                            reconciliation.ResolvedCount,
                            reconciliation.SuppressionExpiredCount);

                        if (failures != null)
                        {
                            _logger.LogWarning("Scan run {ScanRunId} partially completed: {ErrorMessage}", scanRun.Id, failures);
                        }
                    }
                }
                catch (Exception exception) when (!recorded && timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    // Stopped by the time limit, not by the job: a failed scan the loop carries on from
                    TimeoutException timedOut = new TimeoutException(
                        "The scan took longer than " + timeLimit.GetValueOrDefault().TotalMinutes.ToString(CultureInfo.InvariantCulture)
                        + " minutes and was stopped (Scanner:Execution:ScanTimeoutPerRepoMinutes)",
                        exception);
                    await RecordFailureAsync(scanRun.Id, timedOut, commitSha);
                    throw timedOut;
                }
                catch (Exception exception) when (!recorded)
                {
                    await RecordFailureAsync(scanRun.Id, exception, commitSha);
                    throw;
                }
            }
        }

        // One part per failed scanner, in the order they ran, such as "Package scanner failed: nuget.org data could
        // not be downloaded ...". Null when every scanner succeeded.
        private static string? FailureSummary(IReadOnlyList<ScannerResult> results)
        {
            List<string> parts = results
                .Where(result => result.Status == ScannerStatus.Failed)
                .Select(result => result.ScannerType + " scanner failed: " + result.ErrorMessage)
                .ToList();

            return parts.Count == 0 ? null : string.Join("; ", parts);
        }

        // Compares what was found with the findings already stored for the folder and saves the changes
        // in one go (Issue 4 design, Local mode)
        private async Task<ReconciliationResult> ReconcileAsync(
            ScanRun scanRun, IReadOnlyList<RawFinding> found, IReadOnlySet<ScannerType> completedScannerTypes, CancellationToken cancellationToken)
        {
            IReadOnlyList<Finding> stored = await _findings.GetByRepositoryAsync(scanRun.RepositoryId, cancellationToken);

            ReconciliationResult reconciliation = FindingReconciler.Reconcile(stored, found, completedScannerTypes, scanRun, DateTime.UtcNow);
            await _findings.SaveChangesAsync(reconciliation.Changes, cancellationToken);

            LogChanges(reconciliation.Changes);
            return reconciliation;
        }

        // One line per new, reopened and resolved finding, once they are saved. The rule, file and line
        // only: never the matched text or its preview.
        private void LogChanges(FindingChanges changes)
        {
            foreach (Finding finding in changes.NewFindings)
            {
                _logger.LogInformation("New finding {RuleId} in {FilePath} at line {LineNumber}", finding.RuleId, finding.FilePath, finding.LineNumber);
            }

            Dictionary<int, Finding> updatedById = changes.UpdatedFindings.ToDictionary(finding => finding.Id);

            foreach (FindingEvent findingEvent in changes.NewEvents)
            {
                Finding finding = updatedById[findingEvent.FindingId];

                if (findingEvent.EventType == FindingEventType.Reopened)
                {
                    _logger.LogInformation("Reopened finding {RuleId} in {FilePath} at line {LineNumber}", finding.RuleId, finding.FilePath, finding.LineNumber);
                }
                else if (findingEvent.EventType == FindingEventType.Resolved)
                {
                    _logger.LogInformation("Resolved finding {RuleId} in {FilePath} at line {LineNumber}", finding.RuleId, finding.FilePath, finding.LineNumber);
                }
            }
        }

        // Marks the run Failed with the error, so a failed scan is visible in ScanRuns (ADR-013). If that
        // fails too, for example because the database is down, it is logged and the original error is
        // the one that continues. commitSha is null when the clone itself failed (#38).
        private async Task RecordFailureAsync(int scanRunId, Exception failure, string? commitSha)
        {
            try
            {
                ScanRunCompletion completion = new ScanRunCompletion(
                    ScanRunStatus.Failed, commitSha, DateTime.UtcNow, NewCount: 0, ReopenedCount: 0, ResolvedCount: 0, failure.Message, Array.Empty<ScannerResult>());

                // Not the run's token: a cancelled run is still recorded as Failed
                await _scanRuns.CompleteRunAsync(scanRunId, completion, CancellationToken.None);
            }
            catch (Exception recordingFailure)
            {
                _logger.LogWarning(recordingFailure, "Could not mark scan run {ScanRunId} as failed", scanRunId);
            }
        }
    }
}
