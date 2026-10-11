# ADR-027: Skip a repository that is being scanned; take over a run abandoned for longer than the per-repository timeout plus five minutes

- **Status:** Accepted
- **Date:** 2026-10-01
- **Source:** Decided while implementing Issue 7 (#28), task #33

## Context

The nightly and on-demand jobs run the same image and read their own queues (ADR-011), so both can pick up the same repository at once: "Scan now" clicked while the nightly run is scanning it, for example. Two scans of one repository at the same time do no lasting harm, since findings are matched on fingerprint (ADR-004), but they double the work and leave two ScanRuns that each say only half of what happened. ADR-012 builds no locking between executions.

A scan run is saved as Running when it starts and completed when it ends (ADR-013). A run that crashes, or is stopped by its replicaTimeout, never completes, so its row would stay Running for ever, and every later message for that repository would see it.

## Decision

Before scanning a message's repository, the loop reads its Running scan runs (`GetRunningAsync`, on the `(RepositoryId, Status)` index).

- **Any started `ScanTimeoutPerRepoMinutes` + 5 minutes ago or less** (20 minutes by default): another run is scanning it. The message is deleted and logged as skipped, with the ID of the run it gave way to. No new ScanRun is written.
- **Only older ones:** each is marked Failed with "Abandoned: still Running more than 20 minutes after it started, so the run that started it had stopped", and the repository is scanned.
- **On a message's 4th pick-up**, before it is poisoned, Running rows older than the same age are marked Failed with "Moved to <poison queue> after 3 attempts". Younger ones are left alone.

A live scan is stopped at `ScanTimeoutPerRepoMinutes`, so a Running row older than that plus 5 minutes cannot belong to a run that is still alive. The 5 minutes cover saving after a scan stops and small differences between the clocks of the machines that wrote and read the row.

A database error during this check stops the run (exit 1) rather than failing the one message: with the database out of reach, every message would fail the same way.

## Consequences

A repository is scanned once at a time in the normal case, and no ScanRun stays Running for ever. A skipped request is not kept: if the scan it gave way to then fails, the repository waits for the next night or another "Scan now", and the dashboard shows that run as Failed. Two runs that check in the same instant can both scan the repository; this is accepted rather than adding a lock, as ADR-012 decided, and the findings are still not duplicated. Locking with a storage lease or a database row lock was considered and left out as more than this scale needs. If `ScanTimeoutPerRepoMinutes` is raised, the abandoned age rises with it.

## Amendments

**2026-10-09 (Issue 13, #76):** A request already done is dropped before this ADR's check. When a message that was read has a repository with a Completed or PartiallyCompleted scan run from the same lane that started strictly after the message was queued (its `InsertedOn`, which Azure gives to the whole second, so after the end of that second), the message is deleted and logged as `Already done`, with no scan and no new scan run. It is counted as `already done` on the run's last line, apart from `skipped`. The check comes before the 4th-pick-up poison check, so a request already done is never poisoned, and before the Running check above. Failed and Running runs never count, so a scan that failed or crashed is still retried, and a copy that arrives while the first copy is still being scanned is skipped by this ADR as before. Only the same lane counts (agreed 2026-10-04), so a nightly scan never stands in for "Scan now". A request with no `InsertedOn` is scanned. A database error during the check stops the run, as here. This ADR's consequence that "a skipped request is not kept" now has a known gap: a developer who pushes and clicks "Scan now" while that repository is being scanned has the request skipped, though that scan may have cloned before the push. Issue 14 is raised for it.
