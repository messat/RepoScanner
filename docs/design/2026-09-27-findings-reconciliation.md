# Issue 4: Findings reconciliation (design)

- **Status:** Draft, for review
- **Date:** 2026-09-27
- **Work item:** Issue 4, #22 (tasks #23 to #27)
- **Branch:** `feature/22-findings-reconciliation`

## Purpose

Each scan compares what it found with what is already stored for the repository. A problem found again updates its existing row, a new one is added, one that has gone is marked Resolved, and one that comes back is reopened. Without this, every run adds duplicates and nothing is ever resolved. The dashboard depends on it.

Task #24 names "rules 1 to 6 in section 3.6". *Code Repo Analysis Dashboard, Spike Findings & ADRs v2* has no numbered headings; its Data model section (the Finding and FindingEvent entries and "Three reasoning points") is the only place that sets reconciliation rules, and it gives six. This document takes those as rules 1 to 6 and copies them below, so the repository does not depend on the spike document.

## Success criteria

From Issue 4 (#22):

1. A second run against an unchanged fixture creates zero new findings.
2. Removing a planted secret resolves it.
3. A suppressed finding stays suppressed.
4. A failed scanner does not auto-resolve findings of its own type, but other types still resolve.

## Decisions

| # | Question | Decision | Reason |
|---|---|---|---|
| 1 | Where the rules come from | The spike document's Data model section, copied under Reconciliation rules below | See Purpose |
| 2 | The same secret twice in one file | One finding | The fix for a leaked secret is to change it, not to remove copies. The spike document numbers each copy, but then deleting the first copy gives the second its number, and a suppression can land on the wrong copy: the line-number problem again. Recorded in ADR-024 |
| 3 | How much of the failed-scanner rule Issue 4 builds | The rule in the reconciler only. `LocalScanRunner` still fails the whole run when a scanner throws | Carrying on after one scanner fails is Issue 5, task #74. Today a failure stops the run before anything is resolved, so nothing is wrongly resolved |
| 4 | A suppressed finding that is no longer found | Resolved | Not in the code means fixed, whatever the status. Left suppressed, it would return to Open when its date passed, for code that no longer exists |
| 5 | Which scanner types #23 covers | All three | #23 asks for each type. All the identity rules are in one class and one ADR, and Issues 5 and 6 only fill in their fields |
| 6 | The secret formula | Unchanged from Issue 3: `RuleId`, `FilePath`, `SecretHash` | Findings already saved still match, so no local database needs clearing |
| 7 | The repository in the fingerprint | Left out | The unique index is already `(RepositoryId, Fingerprint)`, and decision 6 |
| 8 | What records "last seen" | `LastSeenUtc`, as Issue 2 built it, not the spike's `LastSeenScanRunId` | No migration. The events hold the run IDs |
| 9 | What "seen again" updates | `LastSeenUtc` and `LineNumber` | The spike's "update LastSeenScanRunId and the line pointer". The message, severity and preview stay as first seen |
| 10 | Expired suppressions | Returned to Open, with a `SuppressionExpired` event, when the scan sees them | ADR-019. The dashboard's open query ("not suppressed, or suppression expired") covers the time between the date passing and the next scan |
| 11 | The suppression columns | Never changed by the scanner | ADR-019: the dashboard sets them and the scanner respects them. `Status` says whether a suppression applies; the columns keep the last one for reference |
| 12 | A finding resolved in the dashboard but still in the code | Reopened by the next scan | Rule 4 applies whoever resolved it. The scan is what knows what is in the code |
| 13 | The shape of the reconciler | A static method: stored findings, raw findings and the run in; changes and counts out. No database inside | One test per rule, with no database (ADR-010). Same shape as `SecretHasher` |
| 14 | Where the two classes live | `src/RepoScanner.Scanner/Reconciliation/` | Only the scanner reconciles. Core stays free of logic |
| 15 | What identifies a code-pattern finding | A new `SnippetHash` on `RawFinding`, set by the code-pattern scanner. How the snippet is tidied before hashing is decided in Issue 6 | The spike's "hash of the normalised snippet". The scanner is where the code is read. It is not stored on `Finding`: the fingerprint carries it |
| 16 | What identifies a package finding | `RuleId`, `FilePath`, `PackageName` in lower case, `PackageVersion`, `AdvisoryUrl` | The spike's "project path, package ID, version and advisory ID". `RawFinding` carries the advisory as its URL, and NuGet package IDs ignore case |
| 17 | New ADR numbers | ADR-024 for the fingerprint (#27), ADR-025 for the per-scanner rule (#25) | The next free number is ADR-024 |
| 18 | Where this document lives | `docs/design/` | Same as Issues 2 and 3 |

## Project layout

```
src/RepoScanner.Core/
  Scanning/        RawFinding gains SnippetHash                          #23

src/RepoScanner.Scanner/
  Reconciliation/  FingerprintCalculator                                 #23
                   FindingReconciler, ReconciliationResult               #24, #25
  Local/           LocalScanRunner uses them                             #26

tests/RepoScanner.Scanner.Tests/
  Reconciliation/  fingerprint and reconciler tests                      #23 to #25
  Local/           end-to-end --path tests                               #26

docs/adr/ADR-024-fingerprint-per-scanner-without-line-numbers.md        #27
docs/adr/ADR-025-auto-resolve-only-for-scanners-that-completed.md       #25
```

Classes are `sealed` or `static` and follow ADR-021, as in Issues 2 and 3. Neither new class is registered in the host: both are static, with no dependencies.

## Fingerprint (#23)

```csharp
namespace RepoScanner.Scanner.Reconciliation
{
    public static class FingerprintCalculator
    {
        // SHA-256 of the finding's identity, as 64 lower-case hex characters (Findings.Fingerprint is varchar(64))
        public static string Calculate(RawFinding finding);
    }
}
```

The identity is the finding's parts joined with `|`, hashed as UTF-8:

| Scanner | Parts, in order | Required on `RawFinding` |
|---|---|---|
| `Secret` | `RuleId`, `FilePath`, `SecretHash` | `SecretHash` |
| `Package` | `RuleId`, `FilePath` (the project file), `PackageName` in lower case (invariant), `PackageVersion`, `AdvisoryUrl` | `PackageName`, `PackageVersion`, `AdvisoryUrl` |
| `CodePattern` | `RuleId`, `FilePath`, `SnippetHash` | `SnippetHash` |

- The line number is never part of it, so a finding that moves up or down the file keeps its fingerprint.
- A missing required part throws `ArgumentException` naming it. That is a scanner bug, not something to save.
- The secret formula is the one Issue 3's interim code used (decision 6), and a test proves the two give the same value.
- `RawFinding.SnippetHash` is `string?`, "code-pattern findings only (Issue 6)", like the package properties.

**ADR-024, added in #27: a fingerprint per scanner type, without line numbers.**

- **Context:** a finding must be recognised across scans, so a suppression and its history stay attached to it. Line numbers change with every edit above the finding.
- **Decision:** the parts in the table above, hashed with SHA-256.
- **Consequences:** renaming or moving a file gives new fingerprints, so its findings are resolved and found again as new, and a suppression does not follow them. The spike document accepts this.
- **Differences from the spike document:** the repository is left out, because the unique index is per repository; and a secret has no occurrence number, so the same secret twice in one file is one finding (decision 2).

## Reconciliation rules (#24)

```csharp
namespace RepoScanner.Scanner.Reconciliation
{
    public static class FindingReconciler
    {
        // stored: every finding for the repository, whatever its status, as GetByRepositoryAsync returns them.
        // They are changed in place, so EF Core writes only the columns that changed.
        public static ReconciliationResult Reconcile(
            IReadOnlyList<Finding> stored,
            IReadOnlyList<RawFinding> found,
            IReadOnlySet<ScannerType> completedScannerTypes,   // added in #25
            ScanRun scanRun,
            DateTime nowUtc);
    }

    public sealed record ReconciliationResult(
        FindingChanges Changes,
        int NewCount,
        int ReopenedCount,
        int ResolvedCount,
        int SuppressionExpiredCount);
}
```

Each raw finding is fingerprinted. When two share a fingerprint, the first is kept; the scanners return findings sorted by file and line, so that is the one nearest the top of the file. Each is then matched with the stored finding that has the same fingerprint:

| Rule | Situation | What changes | Event | Counted as |
|---|---|---|---|---|
| 1 | Found, stored as Open | `LastSeenUtc` and `LineNumber` | None | |
| 2 | Found, not stored | A new finding: Open, `FirstSeenUtc` and `LastSeenUtc` now | `Detected` | New |
| 3 | Not found, stored as Open or Suppressed | `Status` Resolved, `ResolvedUtc` now | `Resolved` | Resolved |
| 4 | Found, stored as Resolved | `Status` Open, `ResolvedUtc` cleared, `LastSeenUtc` and `LineNumber` | `Reopened` | Reopened |
| 5 | Found, stored as Suppressed, suppression not expired | `LastSeenUtc` and `LineNumber`; still Suppressed | None | |
| 6 | Found, stored as Suppressed, suppression expired | `Status` Open, `LastSeenUtc` and `LineNumber` | `SuppressionExpired` | Suppression expired |

- A suppression has expired when `SuppressedUntilUtc` is set and is at or before `nowUtc`. Empty means permanent (ADR-019).
- A stored Resolved finding that is not found is left alone.
- Rule 3 covers suppressed findings too (decision 4), and findings resolved in the dashboard reopen under rule 4 (decision 12).
- The suppression columns are never changed (decision 11).
- Every event has the run's `ScanRunId`, `OccurredUtc` of `nowUtc` and `Actor` `scanner`. A new finding's `Detected` event goes in its `Events`, as in Issue 3; the others go in `NewEvents`, with their `FindingId`.
- `UpdatedFindings` holds every stored finding that rules 1 and 3 to 6 changed.

## Failed scanners (#25)

#25 adds `completedScannerTypes` to `Reconcile`: the scanner types that ran and succeeded in this run. Rule 3 then applies only to a stored finding whose `ScannerType` is in the set, so:

| Rule | Situation | What changes |
|---|---|---|
| 7 | Not found, and its scanner type did not complete | Nothing |

This covers a scanner that failed and one that did not run at all. For example, local mode runs only the secret scanner, so it never resolves a package finding.

**ADR-025, added in #25: only a scanner that completed can resolve its findings.**

- **Context:** a scanner that fails or does not run reports nothing, which looks the same as everything being fixed.
- **Decision:** the reconciler is told which scanner types completed, and resolves findings of those types only.
- **Consequences:** while a scanner keeps failing, its findings stay as they were. Its `ScannerResults` row shows `Failed`, and from Issue 5 the run shows `PartiallyCompleted`, so the dashboard can tell the results are stale.
- **Rejected:** resolving anyway, which would mark every finding of a failed scanner as fixed; and skipping reconciliation for the whole run when any scanner fails, which would stop the other scanners' findings resolving while one is broken.

## Local mode (#26)

`LocalScanRunner` replaces its interim code (`SaveNewFindingsAsync`, `InterimFingerprint` and `ToFinding`) with:

1. `GetByRepositoryAsync`, as now.
2. `FindingReconciler.Reconcile`, with every scanner's type as `completedScannerTypes`, because a scanner that throws still fails the whole run (decision 3). A comment says Issue 5 passes only the scanners that succeeded.
3. `SaveChangesAsync` with the result's `Changes`: one save, as now.
4. `CompleteRunAsync` with `NewCount`, `ReopenedCount` and `ResolvedCount` from the result.

It logs one line per new, reopened and resolved finding (rule, file and line, never the text), and a summary line with the numbers found, new, reopened, resolved and with an expired suppression, and the duration.

In the README's "Scan a local folder" section, the line saying resolving and reopening come with Issue 4 is replaced with what a rerun now does.

## Testing

Each rule is written test first: a failing test, then the code that makes it pass.

| Task | Tests |
|---|---|
| #23 | For each scanner type: the fingerprint equals a SHA-256 worked out in the test; a different line number gives the same fingerprint; changing any one part gives a different one; package name case is ignored; each missing required part throws, naming it. The secret fingerprint equals Issue 3's interim value for the same finding |
| #24 | One test per rule 1 to 6. Also: a Resolved finding not found is left alone; two raw findings with one fingerprint give one finding, at the first line; a suppressed finding not found is resolved; a finding resolved in the dashboard and found again is reopened; the counts; the run ID, time and actor on every event; the suppression columns unchanged by every rule |
| #25 | With two scanner types stored, one completed and one not: only the completed type's missing findings are resolved (success criterion 4); a type that did not run at all resolves nothing |
| #26 | `Program.RunAsync` with `--path` and in-memory SQLite, as in Issue 3. The fixture scanned twice: the second run has `NewCount` 0 and adds no rows (criterion 1). A copy of the fixture in a temporary folder: after deleting one file and scanning again, its findings are Resolved, with `Resolved` events and `ResolvedCount` (criterion 2); after restoring it, they are reopened. A finding set to Suppressed in the database stays Suppressed after a scan, with no event (criterion 3). The Issue 3 tests still pass |

- The 80% line coverage gate on the Scanner project covers the two new classes.
- Manual check, as in the victim-repo demo: scan a folder, move one file with secrets out and scan again to see its findings Resolved, then move it back and scan again to see them reopened.

## Order of work

One task at a time, stopping after each for review, then a commit whose message starts "This commit" and ends in its task number (ADR-020):

1. **#22** This design document.
2. **#23** `SnippetHash` on `RawFinding`, and `FingerprintCalculator`.
3. **#27** ADR-024.
4. **#24** `FindingReconciler` and `ReconciliationResult`, rules 1 to 6, without `completedScannerTypes`.
5. **#25** `completedScannerTypes`, rule 7 and ADR-025.
6. **#26** `LocalScanRunner`, the end-to-end tests and the README.

## Not in Issue 4

| Item | Where it is on the board |
|---|---|
| Carrying on after one scanner fails, and marking the run `PartiallyCompleted` | Issue 5, tasks #74 and #32, which pass the scanners that succeeded to the reconciler |
| The package and code-pattern scanners, and how a code snippet is tidied before hashing | Issues 5 and 6 |
| Suppressing and resolving in the dashboard, and its open-findings query | The dashboard, not this Epic |
| The commit SHA on a scan run | Issue 8, task #38 |

## Known limitations

- A renamed or moved file gives its findings new fingerprints: they are resolved and found again as new, and a suppression does not follow them (ADR-024).
- A local folder that is moved or renamed gets a new `Repositories` row (Issue 3).
- Changing a rule's severity or message does not change findings already stored (decision 9).
- While a scanner keeps failing, its findings are not resolved, even if they have been fixed (ADR-025).
