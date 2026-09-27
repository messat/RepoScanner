using System;
using System.Collections.Generic;
using System.Linq;
using RepoScanner.Core.Entities;
using RepoScanner.Core.Enums;
using RepoScanner.Core.Persistence;
using RepoScanner.Core.Scanning;

namespace RepoScanner.Scanner.Reconciliation
{
    // Compares what a scan found with the findings already stored for the repository, and works out
    // what to add, change and record: the rules in the Issue 4 design, from the spike document's Data
    // model section. It touches no database, so each rule can be tested on its own (ADR-010).
    public static class FindingReconciler
    {
        // Events the scanner raises are attributed to it rather than to a person (Issue 2 design)
        public const string ScannerActor = "scanner";

        // stored: every finding for the repository, whatever its status, as GetByRepositoryAsync returns
        // them. They are changed in place, so EF Core, which is tracking them, writes only the columns that
        // changed, and a suppression made in the dashboard meanwhile is kept.
        public static ReconciliationResult Reconcile(IReadOnlyList<Finding> stored, IReadOnlyList<RawFinding> found, ScanRun scanRun, DateTime nowUtc)
        {
            // The unique index on (RepositoryId, Fingerprint) means one stored finding per fingerprint
            Dictionary<string, Finding> storedByFingerprint = stored.ToDictionary(finding => finding.Fingerprint, StringComparer.Ordinal);
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            List<Finding> newFindings = new List<Finding>();
            List<Finding> updatedFindings = new List<Finding>();
            List<FindingEvent> newEvents = new List<FindingEvent>();
            int reopenedCount = 0;
            int suppressionExpiredCount = 0;
            int resolvedCount = 0;

            foreach (RawFinding raw in found)
            {
                string fingerprint = FingerprintCalculator.Calculate(raw);

                // The same finding twice in this run, such as one secret twice in a file (design decision
                // 2). Scanners sort by file and line, so the one kept is the one nearest the top.
                if (!seen.Add(fingerprint))
                {
                    continue;
                }

                // Rule 2: found and not stored
                if (!storedByFingerprint.TryGetValue(fingerprint, out Finding? finding))
                {
                    Finding added = ToFinding(raw, scanRun.RepositoryId, fingerprint, nowUtc);
                    added.Events.Add(NewEvent(FindingEventType.Detected, scanRun, nowUtc));
                    newFindings.Add(added);
                    continue;
                }

                // Rules 1 and 4 to 6: found and stored. The line number is for display only and may have moved.
                finding.LastSeenUtc = nowUtc;
                finding.LineNumber = raw.LineNumber;
                updatedFindings.Add(finding);

                if (finding.Status == FindingStatus.Resolved)
                {
                    // Rule 4, whether a scan or a developer resolved it (design decision 12)
                    finding.Status = FindingStatus.Open;
                    finding.ResolvedUtc = null;
                    newEvents.Add(EventFor(finding, FindingEventType.Reopened, scanRun, nowUtc));
                    reopenedCount++;
                }
                else if (finding.Status == FindingStatus.Suppressed && HasExpired(finding, nowUtc))
                {
                    // Rule 6. The suppression columns are left for reference (design decision 11).
                    finding.Status = FindingStatus.Open;
                    newEvents.Add(EventFor(finding, FindingEventType.SuppressionExpired, scanRun, nowUtc));
                    suppressionExpiredCount++;
                }

                // Rules 1 and 5: an open finding, or a suppression still in force, needs nothing more
            }

            // Rule 3: stored and not found. Open and suppressed findings alike, since code that has gone is
            // fixed (design decision 4). One already resolved is left alone.
            foreach (Finding finding in stored)
            {
                if (seen.Contains(finding.Fingerprint) || finding.Status == FindingStatus.Resolved)
                {
                    continue;
                }

                finding.Status = FindingStatus.Resolved;
                finding.ResolvedUtc = nowUtc;
                updatedFindings.Add(finding);
                newEvents.Add(EventFor(finding, FindingEventType.Resolved, scanRun, nowUtc));
                resolvedCount++;
            }

            FindingChanges changes = new FindingChanges(newFindings, updatedFindings, newEvents);
            return new ReconciliationResult(changes, newFindings.Count, reopenedCount, resolvedCount, suppressionExpiredCount);
        }

        // Empty means suppressed permanently (ADR-019). A suppression ending at this moment has expired.
        private static bool HasExpired(Finding finding, DateTime nowUtc)
        {
            return finding.SuppressedUntilUtc.HasValue && finding.SuppressedUntilUtc.Value <= nowUtc;
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

        // An event for a new finding: EF Core fills in FindingId when the finding is saved with it
        private static FindingEvent NewEvent(FindingEventType eventType, ScanRun scanRun, DateTime nowUtc)
        {
            return new FindingEvent
            {
                ScanRunId = scanRun.Id,
                EventType = eventType,
                OccurredUtc = nowUtc,
                Actor = ScannerActor
            };
        }

        // An event for a finding that is already stored, so it has an Id
        private static FindingEvent EventFor(Finding finding, FindingEventType eventType, ScanRun scanRun, DateTime nowUtc)
        {
            FindingEvent findingEvent = NewEvent(eventType, scanRun, nowUtc);
            findingEvent.FindingId = finding.Id;
            return findingEvent;
        }
    }
}
