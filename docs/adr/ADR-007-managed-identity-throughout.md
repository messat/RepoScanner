# ADR-007: Managed identity throughout

- **Status:** Accepted
- **Source:** Code Repo Analysis Dashboard, Spike Findings & ADRs v2 (canonical for ADR-001 to ADR-019)

## Context

A tool that flags hard-coded connection strings cannot itself hold one.

## Decision

User-assigned managed identities to SQL, Key Vault, Storage queues and the container registry, split into id-scanner for the jobs and id-dashboard for Blazor (ADR-014). Key Vault holds exactly one secret, the Azure DevOps PAT scoped to Code (Read), because Azure DevOps is the one dependency the spike could not authenticate to with an Entra token in the time available. It reaches the job as a secret reference and reaches git as an http.extraheader, never as part of a URL. The pipeline authenticates with Workload Identity Federation, so Azure DevOps holds no secret either.

## Consequences

No password or connection string exists anywhere in configuration, DevOps variables or Key Vault. Key Vault alone would not solve this: authenticating to Key Vault with a secret moves the problem rather than removing it. Accepts the setup cost of CREATE USER ... FROM EXTERNAL PROVIDER and role assignments. Considered and deferred: Azure DevOps accepts a managed identity as an organisation user, which would let id-scanner call the REST API and clone with an Entra token and remove Key Vault from the design entirely. It was not chosen for the EPA because it needs the organisation linked to the tenant, an access level assigned to the identity, and spike time the schedule did not have; it is recorded as the production upgrade and would replace this decision's one remaining secret.
