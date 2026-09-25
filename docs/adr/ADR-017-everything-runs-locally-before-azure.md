# ADR-017: Everything runs locally before Azure

- **Status:** Accepted
- **Source:** Code Repo Analysis Dashboard, Spike Findings & ADRs v2 (canonical for ADR-001 to ADR-019)

## Context

Azure resources are not a development playground, and a TDD loop needs a queue, a database and a credential on the developer's machine.

## Decision

Azurite for queues, SQL Server in Docker for the database, dotnet user-secrets for the PAT, wired together with docker-compose. Every setting is read through IConfiguration. Key Vault and the Azure resources belong to the deployment phase.

## Consequences

Moving to Azure is a configuration-provider swap (environment variables and a Key Vault secret reference), not a code change. Migrations are applied by dotnet ef locally and by the pipeline in Azure; the scanner never applies them (ADR-006). Implementation follows a walking skeleton: data contract first, then one thin end-to-end slice, and the dashboard is not built before the schema it reads.

## Amendments

**2026-09-24:** The local database is the SQL Server instance installed on the developer's machine (SQL Server 2019 Express, `.\SQLEXPRESS`), not SQL Server in Docker. SQL Server is already installed there, and it is reached with Windows authentication, so local work needs no database password. docker-compose runs Azurite and, from Issue 9, the scanner, but no database. Azure still uses Azure SQL Database (ADR-015); both use the EF Core SQL Server provider, so only the connection string differs. The scanner container in Issue 9 reaches the local instance through `host.docker.internal`; Windows authentication is not available from a Linux container, so the container signs in with a local-only SQL login that holds db_datareader and db_datawriter only, the same rights as the runtime identity in ADR-008. Its password lives in the git-ignored `.env` file. The decision is otherwise unchanged.
