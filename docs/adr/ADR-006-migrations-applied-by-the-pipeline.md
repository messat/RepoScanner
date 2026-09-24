# ADR-006: Migrations applied by the pipeline

- **Status:** Accepted
- **Source:** Code Repo Analysis Dashboard, Spike Findings & ADRs v2 (canonical for ADR-001 to ADR-019)

## Context

A job deployment is an image tag update with nothing running to smoke-test. A pipeline can go green while the database still has the old schema, and the failure surfaces at 02:00 when nobody is watching.

## Decision

A Migrate stage before the deploy stage, generating an idempotent script with dotnet ef migrations script --idempotent and applying it with the pipeline identity.

## Consequences

Database.Migrate() at startup was rejected on three grounds: it requires the runtime identity to hold ALTER TABLE rights on a database containing client secrets, directly contradicting the tool's own security case; replicas race each other when parallelism is above one; and it re-checks on every execution forever. Destructive changes follow expand/contract - add, migrate code, backfill, remove in a later release - never in one deployment.
