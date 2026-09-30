# ADR-013: Delete after upsert, poison queue after three failures

- **Status:** Accepted
- **Source:** Code Repo Analysis Dashboard, Spike Findings & ADRs v2 (canonical for ADR-001 to ADR-019)

## Context

Storage queues are at-least-once. A message deleted before the scan is recorded vanishes silently if the scan fails; a message never deleted reappears after every visibility timeout, and one bad repository burns execution minutes all night.

## Decision

DeleteMessage runs after the ScanRun and findings upsert have committed. On receive, a DequeueCount above 3 writes ScanRun.Status = Failed with the exception message, copies the message to scan-nightly-poison or scan-ondemand-poison, deletes the original and exits 0. visibilityTimeout is at least replicaTimeout plus a buffer; replicaRetryLimit is 1.

## Consequences

A failed scan is always visible in ScanRun, and a repository that cannot be scanned costs at most three cycles. The poison queue needs its own alert. A shorter visibility timeout with an UpdateMessage heartbeat was considered and rejected as unnecessary at this scale. Log Analytics is the cross-check; the exception message is stored so it is not the only record.

## Amendments

**2026-09-30:** Each lane has its own poison queue, as decided. A malformed message, including one with a `schemaVersion` the scanner does not read, is moved to it on its first read rather than after three attempts, and gets no ScanRun, since its repository details cannot be trusted: the one exception to "a failed scan is always visible in ScanRun". The poison copy keeps the body unchanged, so it can be moved back, and expires after 30 days (`Scanner:Queue:PoisonMessageTimeToLiveDays`), where Azure's default is 7. It is sent before the original is deleted, so the message is never lost. In `Development` the visibility timeout is 1 minute, so a retry can be watched locally; in Azure it stays at least replicaTimeout plus a buffer (Issue 7 design, #30). The decision is otherwise unchanged.
