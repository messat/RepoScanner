# ADR-013: Delete after upsert, poison queue after three failures

- **Status:** Accepted
- **Source:** Code Repo Analysis Dashboard, Spike Findings & ADRs v2 (canonical for ADR-001 to ADR-019)

## Context

Storage queues are at-least-once. A message deleted before the scan is recorded vanishes silently if the scan fails; a message never deleted reappears after every visibility timeout, and one bad repository burns execution minutes all night.

## Decision

DeleteMessage runs after the ScanRun and findings upsert have committed. On receive, a DequeueCount above 3 writes ScanRun.Status = Failed with the exception message, copies the message to scan-nightly-poison or scan-ondemand-poison, deletes the original and exits 0. visibilityTimeout is at least replicaTimeout plus a buffer; replicaRetryLimit is 1.

## Consequences

A failed scan is always visible in ScanRun, and a repository that cannot be scanned costs at most three cycles. The poison queue needs its own alert. A shorter visibility timeout with an UpdateMessage heartbeat was considered and rejected as unnecessary at this scale. Log Analytics is the cross-check; the exception message is stored so it is not the only record.
