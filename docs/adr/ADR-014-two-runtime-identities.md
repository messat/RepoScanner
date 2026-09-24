# ADR-014: Two runtime identities

- **Status:** Accepted
- **Source:** Code Repo Analysis Dashboard, Spike Findings & ADRs v2 (canonical for ADR-001 to ADR-019)

## Context

One identity across every runtime component would let the internet-facing dashboard read the PAT and consume the scan queues. The dashboard needs neither.

## Decision

User-assigned identities: id-scanner for the enqueuer and both scanner jobs, id-dashboard for Blazor. Queue roles are scoped to the individual queue, not the storage account (table in Security design). User-assigned rather than system-assigned so the identity exists before the job does and AcrPull can be granted at creation.

## Consequences

Blazor holds no Key Vault role and can only send to scan-ondemand. The scale rule authenticates as id-scanner and needs Storage Queue Data Reader for the queue-length call, which Message Processor does not include; whether that role alone is sufficient is the one remaining mini-spike, since Spike 2 used Contributor. Two identities to create and assign in the pipeline.
