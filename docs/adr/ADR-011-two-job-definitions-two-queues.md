# ADR-011: Two job definitions, two queues

- **Status:** Accepted
- **Source:** Code Repo Analysis Dashboard, Spike Findings & ADRs v2 (canonical for ADR-001 to ADR-019)

## Context

Scan now must start promptly and the nightly run must never be delayed by on-demand traffic. maxExecutions is one cap shared by every scale rule on a job, so a second rule on the same job cannot reserve capacity for either lane.

## Decision

job-scanner-nightly bound to scan-nightly and job-scanner-ondemand bound to scan-ondemand. Same image, same identity, one environment variable differs: QUEUE_NAME. Proven in Spike 2 as job-scanner-spike and job-ondemand-spike.

## Consequences

No queue-selection code exists; each job reads only its own queue and on-demand never falls back to nightly. On-demand scans run strictly one at a time and further clicks wait in the queue. Cost is unchanged because billing is per vCPU-second, not per job definition; the pipeline updates two image tags instead of one. Two scale rules on one job with a queue-preference fallback was designed first and rejected the moment the isolation requirement was stated.

## Amendments

**2026-09-24:** The setting that differs between the two jobs is `Scanner:Queue:Name` (environment variable `Scanner__Queue__Name`), not `QUEUE_NAME` as used in Spike 2. It sits with the rest of the scanner's settings under `Scanner:` and is validated at start-up (see ADR-022). The decision is otherwise unchanged.
