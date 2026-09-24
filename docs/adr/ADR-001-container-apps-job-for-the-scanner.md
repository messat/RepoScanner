# ADR-001: Container Apps Job for the scanner

- **Status:** Accepted
- **Source:** Code Repo Analysis Dashboard, Spike Findings & ADRs v2 (canonical for ADR-001 to ADR-019)

## Context

The proposal listed Container Apps jobs, Azure Functions and Hangfire without choosing. Scan duration is unknown but could exceed ten minutes on a large repository.

## Decision

Container Apps Job, Schedule and Event triggers.

## Consequences

Configurable timeout rather than a fixed ceiling; scales to zero with no idle cost. Accepts a container registry and Docker build step as overhead, and a ~23-second cold start per execution.
