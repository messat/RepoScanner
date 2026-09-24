# ADR-017: Everything runs locally before Azure

- **Status:** Accepted
- **Source:** Code Repo Analysis Dashboard, Spike Findings & ADRs v2 (canonical for ADR-001 to ADR-019)

## Context

Azure resources are not a development playground, and a TDD loop needs a queue, a database and a credential on the developer's machine.

## Decision

Azurite for queues, SQL Server in Docker for the database, dotnet user-secrets for the PAT, wired together with docker-compose. Every setting is read through IConfiguration. Key Vault and the Azure resources belong to the deployment phase.

## Consequences

Moving to Azure is a configuration-provider swap (environment variables and a Key Vault secret reference), not a code change. Migrations are applied by dotnet ef locally and by the pipeline in Azure; the scanner never applies them (ADR-006). Implementation follows a walking skeleton: data contract first, then one thin end-to-end slice, and the dashboard is not built before the schema it reads.
