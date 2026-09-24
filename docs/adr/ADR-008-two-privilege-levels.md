# ADR-008: Two privilege levels

- **Status:** Accepted
- **Source:** Code Repo Analysis Dashboard, Spike Findings & ADRs v2 (canonical for ADR-001 to ADR-019)

## Context

Schema changes and runtime data access need different rights.

## Decision

Pipeline identity holds DDL rights and runs once per deployment. Runtime identities hold db_datareader and db_datawriter only.

## Consequences

Neither the scanner nor the dashboard can alter schema under any circumstances. This split is the substance of the security argument, more so than the use of managed identity on its own.
