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

**2026-10-01 (Issue 8):** Locally the PAT is the user environment variable `Scanner__AzureDevOps__Pat`, not dotnet user-secrets: it needs no set-up in the project, and docker-compose can pass it to the scanner container in Issue 9. In Azure it is still a Key Vault secret reference, which reaches the job as the same environment variable. The decision is otherwise unchanged.

**2026-10-01 (Issue 9):** there is no docker-compose. With Azurite and SQL Server both on the developer's machine, as their Azure counterparts are outside the Container Apps environment, Compose would hold only the scanner. The scanner image is built with `docker build` and run with `docker run --env-file .env`. The container reaches Azurite and SQL Server through `host.docker.internal`, which Docker Desktop passes to the machine's own 127.0.0.1. Runs on Windows read user environment variables; the container reads only the git-ignored `.env` file, which holds its own copy of the PAT and the SQL login's connection string. This replaces the docker-compose parts of the 2026-09-24 and 2026-10-01 amendments. The decision is otherwise unchanged.
