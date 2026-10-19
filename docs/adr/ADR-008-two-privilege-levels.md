# ADR-008: Two privilege levels

- **Status:** Accepted
- **Source:** Code Repo Analysis Dashboard, Spike Findings & ADRs v2 (canonical for ADR-001 to ADR-019)

## Context

Schema changes and runtime data access need different rights.

## Decision

Pipeline identity holds DDL rights and runs once per deployment. Runtime identities hold db_datareader and db_datawriter only.

## Consequences

Neither the scanner nor the dashboard can alter schema under any circumstances. This split is the substance of the security argument, more so than the use of managed identity on its own.

## Amendments

**2026-10-04 (Issue 11, ADR-029):** this release has no pipeline identity. Locally, `dotnet ef` applies migrations with the developer's own Windows login, and the container's `repo_scanner_local` login holds db_datareader and db_datawriter only (README, Database and migrations), so the scanner still cannot change the schema. The decision is otherwise unchanged.

**2026-10-19 (Issue 12, ADR-030):** ADR-030 supersedes ADR-029, so the 2026-10-04 amendment above no longer applies. In Azure, `id-pipeline` holds db_ddladmin; `id-scanner` and `id-dashboard` hold db_datareader and db_datawriter, added by hand (README, Database users in Azure). The decision is otherwise unchanged.
