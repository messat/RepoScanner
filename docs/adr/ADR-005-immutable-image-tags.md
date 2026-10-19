# ADR-005: Immutable image tags

- **Status:** Accepted
- **Source:** Code Repo Analysis Dashboard, Spike Findings & ADRs v2 (canonical for ADR-001 to ADR-019)

## Context

Deploying a job means updating its image tag, which is the entire deployment operation.

## Decision

Tag with $(Build.BuildId), never deploy from latest.

## Consequences

Every running job traces to an exact build and commit; rollback is redeploying a previous tag. latest is a convenience pointer only - it moves, so it cannot be rolled back to and does not identify what is running.

## Amendments

**2026-10-04 (Issue 11, ADR-029):** this release has no pipeline, so there is no build ID to tag with. The scanner image is built and tagged by hand, `reposcanner:v1` (Issue 9), and is never pushed to a registry. The decision is otherwise unchanged.

**2026-10-19 (Issue 12, ADR-030):** ADR-030 supersedes ADR-029, so the 2026-10-04 amendment above no longer applies. Images are tagged `$(Build.BuildId)` by RepoScanner's pipeline and the enqueuer's. The decision is otherwise unchanged.
