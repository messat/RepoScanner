using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RepoScanner.Core.Entities;
using RepoScanner.Core.Enums;
using RepoScanner.Core.Persistence;
using RepoScanner.Core.Scanning;

namespace RepoScanner.Scanner.Local
{
    // Runs the scanners on a folder on this machine and saves what they find (--path, #20): the first
    // vertical slice, with no queue and no clone. The queue loop arrives in Issue 5.
    public sealed class LocalScanRunner
    {
        // Events the scanner raises are attributed to it rather than to a person (Issue 2 design)
        public const string ScannerActor = "scanner";

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

            // Every registered scanner: only SecretScanner in Issue 3, all three once Issues 7 and 8
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

                foreach (IScanner scanner in _scanners)
                {
                    Stopwatch scannerDuration = Stopwatch.StartNew();
                    IReadOnlyList<RawFinding> scannerFindings = await scanner.ScanAsync(folder, cancellationToken);

                    results.Add(new ScannerResult
                    {
                        ScannerType = scanner.ScannerType,
                        Status = ScannerStatus.Succeeded,
                        FindingCount = scannerFindings.Count,
                        DurationMs = (int)scannerDuration.ElapsedMilliseconds
                    });
                    found.AddRange(scannerFindings);
                }

                int newCount = await SaveNewFindingsAsync(scanRun, found, cancellationToken);

                ScanRunCompletion completion = new ScanRunCompletion(
                    ScanRunStatus.Completed, CommitSha: null, DateTime.UtcNow, newCount, ReopenedCount: 0, ResolvedCount: 0, ErrorMessage: null, results);
                await _scanRuns.CompleteRunAsync(scanRun.Id, completion, cancellationToken);

                _logger.LogInformation("Scan run {ScanRunId} completed in {DurationMs} ms: {FoundCount} found, {NewCount} new, {KnownCount} already stored",
                    scanRun.Id, duration.ElapsedMilliseconds, found.Count, newCount, found.Count - newCount);
            }
            catch (Exception exception)
            {
                await RecordFailureAsync(scanRun.Id, exception);
                throw;
            }
        }

        // Interim, Issue 3 only (design decision 2): findings already stored, or repeated within this
        // run, are skipped, so running the same folder twice adds nothing. Replaced by
        // FingerprintCalculator (#23) and FindingReconciler (#24) when #26 wires them into local mode.
        private async Task<int> SaveNewFindingsAsync(ScanRun scanRun, IReadOnlyList<RawFinding> found, CancellationToken cancellationToken)
        {
            IReadOnlyList<Finding> stored = await _findings.GetByRepositoryAsync(scanRun.RepositoryId, cancellationToken);
            HashSet<string> seen = new HashSet<string>(stored.Select(finding => finding.Fingerprint), StringComparer.Ordinal);
            DateTime nowUtc = DateTime.UtcNow;
            List<Finding> newFindings = new List<Finding>();

            foreach (RawFinding raw in found)
            {
                string fingerprint = InterimFingerprint(raw);

                // False when the fingerprint is already stored, or was already added in this run
                if (!seen.Add(fingerprint))
                {
                    continue;
                }

                Finding finding = ToFinding(raw, scanRun.RepositoryId, fingerprint, nowUtc);
                finding.Events.Add(new FindingEvent
                {
                    ScanRunId = scanRun.Id,
                    EventType = FindingEventType.Detected,
                    OccurredUtc = nowUtc,
                    Actor = ScannerActor
                });
                newFindings.Add(finding);

                // The rule, file and line only: never the matched text or its preview
                _logger.LogInformation("New finding {RuleId} in {FilePath} at line {LineNumber}", raw.RuleId, raw.FilePath, raw.LineNumber);
            }

            await _findings.SaveChangesAsync(new FindingChanges(newFindings, Array.Empty<Finding>(), Array.Empty<FindingEvent>()), cancellationToken);
            return newFindings.Count;
        }

        // SHA-256, in lower-case hex, of the rule, the file and the secret's hash. No line number, so a
        // finding that moves down the file keeps its fingerprint, as #27 will require.
        private static string InterimFingerprint(RawFinding raw)
        {
            string identity = raw.RuleId + "|" + raw.FilePath + "|" + raw.SecretHash;
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        }

        private static Finding ToFinding(RawFinding raw, int repositoryId, string fingerprint, DateTime nowUtc)
        {
            return new Finding
            {
                RepositoryId = repositoryId,
                Fingerprint = fingerprint,
                ScannerType = raw.ScannerType,
                RuleId = raw.RuleId,
                Severity = raw.Severity,
                FilePath = raw.FilePath,
                LineNumber = raw.LineNumber,
                Message = raw.Message,
                MaskedPreview = raw.MaskedPreview,
                SecretHash = raw.SecretHash,
                PackageName = raw.PackageName,
                PackageVersion = raw.PackageVersion,
                AdvisoryUrl = raw.AdvisoryUrl,
                Status = FindingStatus.Open,
                FirstSeenUtc = nowUtc,
                LastSeenUtc = nowUtc
            };
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
