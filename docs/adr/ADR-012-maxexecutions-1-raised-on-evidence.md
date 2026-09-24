# ADR-012: maxExecutions 1, raised on evidence

- **Status:** Accepted
- **Source:** Code Repo Analysis Dashboard, Spike Findings & ADRs v2 (canonical for ADR-001 to ADR-019)

## Context

Concurrency does not stress the replicas, which are isolated containers. It stresses what they call: Azure SQL Basic at 5 DTU, nuget.org rate limits and Azure DevOps clone throttling. None has been load-tested.

## Decision

maxExecutions 1 on both jobs. Raise to 3 if the nightly window exceeds 30 minutes or Scan now waits behind nightly. One CLI line, no code change, no redeploy.

## Consequences

Nightly runs sequentially: window ≈ N × (scan time + 30 s poll + cold start), so 40 repositories at 6 minutes each is about four hours from 02:00. ScanRun durations provide the evidence to raise it. No cross-execution locking is built. The counter-test at maxExecutions 3 during Spike 2 shows the value was chosen, not defaulted.
