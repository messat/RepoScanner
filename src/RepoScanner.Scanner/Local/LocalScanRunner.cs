using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RepoScanner.Core.Entities;
using RepoScanner.Core.Enums;
using RepoScanner.Core.Persistence;
using RepoScanner.Core.Scanning;
using RepoScanner.Scanner.Reconciliation;

namespace RepoScanner.Scanner.Local
{
    // Runs the scanners on a folder on this machine and reconciles what they find with the findings
    // already stored for it (--path, #20 and #26): no queue and no clone. The queue loop arrives in Issue 7.
    public sealed class LocalScanRunner
    {
        // Local folders are kept under this project name, so they are easy to tell apart from Azure
        // DevOps repositories in the Repositories table
        public const string LocalProjectName = "Local";

        public const string LocalBranchName = "local";

        private readonly IScanRunRepository _scanRuns;
        private readonly IFindingRepository _findings;
        private readonly IReadOnlyList<IScanner> _scanners;
        private readonly ILogger<LocalScanRunner> _logger;

        public LocalScanRunner(IScanRunRepository scanRuns, IFindingRepository findings, IEnumerable<IScanner> scanners, ILogger<LocalScanRunner> logger)
        {
            _scanRuns = scanRuns;
            _findings = findings;

            // Every registered scanner: only SecretScanner in Issue 3, all three once Issues 5 and 6
            // register theirs. Asking for a single IScanner would silently get the last one registered.
            _scanners = scanners.ToList();
            _logger = logger;
        }

        // A local folder has no Azure DevOps ID, so it gets one made from its full path: the first
        // 16 bytes of the SHA-256 of the lower-cased path. The same folder always maps to the same
        // Repositories row, however it is written (design decision 11).
        public static RepositoryDetails DescribeFolder(string folder)
        {
            string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(fullPath.ToLowerInvariant()));
            string name = Path.GetFileName(fullPath);

            // A drive root such as C:\ has no folder name, so its path is used instead
            return new RepositoryDetails(new Guid(hash.AsSpan(0, 16)), LocalProjectName, name.Length > 0 ? name : fullPath, LocalBranchName);
        }

        public async Task RunAsync(string folder, CancellationToken cancellationToken)
        {
            RepositoryDetails repository = DescribeFolder(folder);
            ScanRun scanRun = await _scanRuns.StartRunAsync(repository, ScanTrigger.OnDemand, Guid.NewGuid(), DateTime.UtcNow, cancellationToken);
            _logger.LogInformation("Scanning {Folder} as scan run {ScanRunId}", folder, scanRun.Id);

            Stopwatch duration = Stopwatch.StartNew();

            // Set once the run's outcome is saved, so a failure rethrown after that is not recorded a second time
            bool recorded = false;
            try
            {
                List<RawFinding> found = new List<RawFinding>();
                List<ScannerResult> results = new List<ScannerResult>();

                // Only these can resolve their findings: one that failed found nothing because it did not look,
                // not because everything was fixed (ADR-025, rule 7)
                HashSet<ScannerType> completedScannerTypes = new HashSet<ScannerType>();
                Exception? firstFailure = null;

                foreach (IScanner scanner in _scanners)
                {
                    Stopwatch scannerDuration = Stopwatch.StartNew();
                    IReadOnlyList<RawFinding> scannerFindings;
                    try
                    {
                        scannerFindings = await scanner.ScanAsync(folder, cancellationToken);
                    }
                    catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
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

                // Which scanners failed and why, for ScanRun.ErrorMessage (ADR-013); null when none failed
                string? failures = FailureSummary(results);

                if (firstFailure != null && completedScannerTypes.Count == 0)
                {
                    // Every scanner failed, so there is nothing to reconcile. The run is Failed with every scanner's
                    // result, and the first failure goes on to Program, which exits with 1 (#32).
                    ScanRunCompletion failed = new ScanRunCompletion(
                        ScanRunStatus.Failed, CommitSha: null, DateTime.UtcNow, NewCount: 0, ReopenedCount: 0, ResolvedCount: 0, failures, results);
                    await _scanRuns.CompleteRunAsync(scanRun.Id, failed, cancellationToken);
                    recorded = true;

                    ExceptionDispatchInfo.Throw(firstFailure);
                }

                ReconciliationResult reconciliation = await ReconcileAsync(scanRun, found, completedScannerTypes, cancellationToken);

                // Some scanners failed and some succeeded: the findings of those that succeeded are saved, and the
                // run says the rest are out of date (#32). The process still exits with 0: the run did its job.
                ScanRunStatus status = failures == null ? ScanRunStatus.Completed : ScanRunStatus.PartiallyCompleted;
                ScanRunCompletion completion = new ScanRunCompletion(
                    status,
                    CommitSha: null,
                    DateTime.UtcNow,
                    reconciliation.NewCount,
                    reconciliation.ReopenedCount,
                    reconciliation.ResolvedCount,
                    failures,
                    results);
                await _scanRuns.CompleteRunAsync(scanRun.Id, completion, cancellationToken);
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
            catch (Exception exception) when (!recorded)
            {
                await RecordFailureAsync(scanRun.Id, exception);
                throw;
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
        // the one that continues.
        private async Task RecordFailureAsync(int scanRunId, Exception failure)
        {
            try
            {
                ScanRunCompletion completion = new ScanRunCompletion(
                    ScanRunStatus.Failed, CommitSha: null, DateTime.UtcNow, NewCount: 0, ReopenedCount: 0, ResolvedCount: 0, failure.Message, Array.Empty<ScannerResult>());

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
