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

                // With no scanner left, the run fails as before; #32 records it with every scanner's result
                if (firstFailure != null && completedScannerTypes.Count == 0)
                {
                    ExceptionDispatchInfo.Throw(firstFailure);
                }

                ReconciliationResult reconciliation = await ReconcileAsync(scanRun, found, completedScannerTypes, cancellationToken);

                ScanRunCompletion completion = new ScanRunCompletion(
                    ScanRunStatus.Completed,
                    CommitSha: null,
                    DateTime.UtcNow,
                    reconciliation.NewCount,
                    reconciliation.ReopenedCount,
                    reconciliation.ResolvedCount,
                    ErrorMessage: null,
                    results);
                await _scanRuns.CompleteRunAsync(scanRun.Id, completion, cancellationToken);

                _logger.LogInformation(
                    "Scan run {ScanRunId} completed in {DurationMs} ms: {FoundCount} found, {NewCount} new, {ReopenedCount} reopened, {ResolvedCount} resolved, {SuppressionExpiredCount} suppressions expired",
                    scanRun.Id,
                    duration.ElapsedMilliseconds,
                    found.Count,
                    reconciliation.NewCount,
                    reconciliation.ReopenedCount,
                    reconciliation.ResolvedCount,
                    reconciliation.SuppressionExpiredCount);
            }
            catch (Exception exception)
            {
                await RecordFailureAsync(scanRun.Id, exception);
                throw;
            }
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
