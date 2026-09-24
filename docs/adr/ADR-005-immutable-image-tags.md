# ADR-005: Immutable image tags

- **Status:** Accepted
- **Source:** Code Repo Analysis Dashboard, Spike Findings & ADRs v2 (canonical for ADR-001 to ADR-019)

## Context

Deploying a job means updating its image tag, which is the entire deployment operation.

## Decision

Tag with $(Build.BuildId), never deploy from latest.

## Consequences

Every running job traces to an exact build and commit; rollback is redeploying a previous tag. latest is a convenience pointer only - it moves, so it cannot be rolled back to and does not identify what is running.
