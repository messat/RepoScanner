# Repo Scanner

A .NET 10 console app that scans Blacklight's Azure DevOps repositories for hard-coded secrets, vulnerable NuGet packages and insecure code patterns, and stores the findings in Azure SQL for the Code Repo Analysis Dashboard. It runs as an Azure Container Apps Job: it processes the queued repositories and exits.

> Work in progress. This README grows as each feature is added.

## Architecture

![Scanner architecture: enqueuer and scanner Container Apps Jobs, storage queues, Azure DevOps, nuget.org, Key Vault, Azure SQL and the Blazor dashboard](docs/images/scanner-architecture-v2.png)

The enqueuer job puts one message per repository on a storage queue. The scanner job starts when messages arrive, clones each repository once, runs the secret, package and code-pattern scanners, and upserts the findings into Azure SQL. The Blazor dashboard reads the findings and lets developers suppress or resolve them. This repository contains the scanner job and the shared data library.

## Project dependencies

```
RepoScanner.Scanner ──► RepoScanner.Data ──► RepoScanner.Core
         │                                          ▲
         └──────────────────────────────────────────┘
```

- **Core** depends on nothing, so it can be used by any project (including the dashboard) without pulling in databases, Azure or Roslyn.
- **Data** uses Core's types to store them in SQL.
- **Scanner** uses both.
- References only point towards Core. A reference the other way creates a loop, which the build rejects (`MSB4006: circular dependency`).

## Prerequisites

- .NET 10 SDK (the version is pinned in `global.json`)
- Git 2.31 or later on PATH: queue mode runs it to clone, and gives it the PAT through `GIT_CONFIG_COUNT`. The tests run it too, against repositories on disk, with no network
- SQL Server installed on your machine (Express, Developer or a full edition), reached with Windows authentication. Only needed to create and use the local database; the tests do not need it
- SQL Server Management Studio (optional), to look at the tables
- To scan from the queue locally: [Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite), the local storage emulator, installed with npm; the [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli), for `scripts/enqueue-local.ps1`; and Azure Storage Explorer (optional), to look at the queues
- To run in a container: [Docker Desktop](https://www.docker.com/products/docker-desktop/), with Linux containers, to build and run the scanner image; and [sqlcmd](https://learn.microsoft.com/sql/tools/sqlcmd/sqlcmd-utility), the SQL Server command-line tool, which SQL Server's installer usually adds, for `scripts/test-container.ps1` and to check the container's login

## Build and run

```bash
dotnet build RepoScanner.sln
```

```bash
dotnet run --project src/RepoScanner.Scanner
```

```bash
dotnet test RepoScanner.sln
```

Without `--path`, `dotnet run` reads its queue: see [Scan from the queue locally](#scan-from-the-queue-locally).

`dotnet test` also measures line coverage (coverlet) and fails if Core or Scanner drop below 80%. Data's coverage is reported but not enforced, and leaves out the generated `Migrations/` code. Reports are written to `tests/<project>/TestResults/coverage/coverage.cobertura.xml` (git-ignored).

The tests need no database server: the Data tests use a new in-memory SQLite database for each test.

The coverage table per project is hidden by the .NET terminal logger in an interactive terminal. To see it, and each test by name:

```bash
dotnet test --tl:off --logger "console;verbosity=normal"
```

## Configuration

Settings are read from `appsettings.json`, then `appsettings.{Environment}.json`, then environment variables, then command-line arguments. Later sources win. `dotnet run` uses the `Development` environment (`Properties/launchSettings.json`), which points at local Azurite.

The app checks its settings when it starts and refuses to run if any are missing or invalid.

| Setting | Required | Default | Notes |
|---|---|---|---|
| `Scanner:Queue:Name` | Yes | | Exactly `scan-nightly` or `scan-ondemand`: the queue is what says whether a scan is nightly or on demand ([Issue 7 design](docs/design/2026-09-29-queue-consumer-and-execution-loop.md)) |
| `Scanner:Queue:ConnectionString` | One of these two | | Local only: `UseDevelopmentStorage=true` (Azurite) |
| `Scanner:Queue:ServiceUri` | One of these two | | Azure only, used with managed identity |
| `Scanner:Queue:PoisonQueueName` | | `<Name>-poison` | Where failing messages are moved. One per lane, e.g. `scan-nightly-poison` (ADR-013) |
| `Scanner:Queue:MaxDequeueCount` | | `3` | Attempts before a message is moved to the poison queue (ADR-013) |
| `Scanner:Queue:PoisonMessageTimeToLiveDays` | | `30` | How long a poisoned message is kept, 1 to 365 days. Azure's own default is 7 |
| `Scanner:Execution:MaxRunDurationMinutes` | | `50` | One run's time budget. No repository is started unless `ScanTimeoutPerRepoMinutes` still fits in what is left, so the run stops before the job's 60-minute limit |
| `Scanner:Execution:ScanTimeoutPerRepoMinutes` | | `15` | One repository's scan is stopped, and its run marked `Failed`, after this long. Plus 5 minutes, a `Running` scan run counts as abandoned ([ADR-027](docs/adr/ADR-027-skip-a-repository-being-scanned-take-over-an-abandoned-run.md)) |
| `Scanner:Execution:VisibilityTimeoutMinutes` | | `65`, and `1` in `Development` | How long a taken message is hidden from other runs. In Azure, the job's 60-minute limit plus 5 (ADR-013); 1 minute locally, so a retry can be watched |
| `Scanner:AzureDevOps:OrganizationUrl` | Yes, in queue mode | `https://dev.azure.com/BlacklightInternalsMo` in `Development` | The organisation repositories are cloned from: an `https` URL with no user name in it, one per scanner deployment ([Issue 8 design](docs/design/2026-10-01-repository-acquisition.md)) |
| `Scanner:AzureDevOps:Pat` | Yes, in queue mode | | A personal access token with Code (Read) only, shared with the enqueuer. Locally, the `Scanner__AzureDevOps__Pat` user environment variable; in Azure, a Key Vault secret reference. Never in an appsettings file (ADR-007) |
| `Scanner:AzureDevOps:CloneTimeoutMinutes` | | `8` | A clone still going after this long is stopped and its scan run marked `Failed` (ADR-016). `ScanTimeoutPerRepoMinutes` still covers the clone and the scan together |
| `Scanner:Database:ConnectionString` | Yes | | The findings database. Locally, the `Scanner__Database__ConnectionString` user environment variable (see [Database and migrations](#database-and-migrations)); never in an appsettings file (ADR-007) |
| `Scanner:Logging:Format` | | `Text` in `Development`, `Json` elsewhere | `Text` or `Json`, in any case: one JSON object per line for Log Analytics, or readable text ([ADR-028](docs/adr/ADR-028-json-log-lines-on-stdout-outside-development.md)) |

With `--path`, only the database setting is needed: the queue, execution and Azure DevOps settings are for queue mode.

As environment variables, `:` becomes `__`, for example:

```powershell
$env:Scanner__Queue__Name = "scan-ondemand"
```

The scanner and the `dotnet ef` tool read the same database connection string: see [Database and migrations](#database-and-migrations).

## Database and migrations

The findings are stored in a SQL Server database called `RepoScanner`: locally, on the SQL Server installed on your machine (not in Docker, [ADR-017](docs/adr/ADR-017-everything-runs-locally-before-azure.md)); in Azure, on Azure SQL Database ([ADR-015](docs/adr/ADR-015-azure-sql-basic-in-uk-west.md)). Both use the same EF Core SQL Server provider, so only the connection string differs. The tables and the reasons for them are in the [Issue 2 design](docs/design/2026-09-24-data-model-and-persistence.md).

### One-off setup

1. Install the pinned `dotnet ef` tool (from `.config/dotnet-tools.json`):

   ```bash
   dotnet tool restore
   ```

2. Save the connection string as a Windows user environment variable. It uses Windows authentication, so it holds no password, and it is never stored in the repository:

   ```bash
   setx Scanner__Database__ConnectionString "Server=.\SQLEXPRESS;Database=RepoScanner;Trusted_Connection=True;TrustServerCertificate=True"
   ```

   Change `.\SQLEXPRESS` if your SQL Server has another name (the name at the top of SSMS Object Explorer; `.` means this computer). `TrustServerCertificate=True` accepts the self-signed certificate of a local SQL Server and is for local use only.

3. Open a new terminal (and restart Visual Studio): programs that were already running do not see a new environment variable.

### Create or update the database

```bash
dotnet ef database update --project src/RepoScanner.Data --startup-project src/RepoScanner.Data
```

The first run creates the `RepoScanner` database and its tables; later runs apply any new migrations. Do not create the database by hand: building it from the migrations alone is how we know a fresh database can always be stood up.

### For the scanner container

Only needed to [run in a container](#run-in-a-container). A Linux container cannot use Windows authentication, so the container signs in with a SQL login of its own, `repo_scanner_local`. It holds db_datareader and db_datawriter only, the same rights as the scanner in Azure ([ADR-008](docs/adr/ADR-008-two-privilege-levels.md)), so it can read and write rows but not change tables; `dotnet ef` keeps using Windows authentication. Once, after the database exists:

1. **TCP/IP on port 1433.** In SQL Server Configuration Manager, SQL Server Network Configuration, Protocols for SQLEXPRESS: set TCP/IP to Enabled. In its properties, IP Addresses tab, under IPAll: TCP Dynamic Ports empty and TCP Port `1433`. Restart the SQL Server (SQLEXPRESS) service. The container connects by port, as `host.docker.internal,1433`, since an instance name such as `.\SQLEXPRESS` only works on this machine.
2. **SQL Server and Windows Authentication mode.** In SSMS, right-click the server, Properties, Security. Restart the service if you changed it. Leave the `sa` login disabled.
3. **A password.** Letters and digits only, since `;`, `=` or a quote would break the connection string. This makes a 64-character one and copies it to the clipboard without showing it:

   ```powershell
   ([guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N').ToUpper()) | Set-Clipboard
   ```

4. **The login.** In SSMS, a new query: paste this, replace `<password>` with the clipboard, run it, then close the tab without saving, so the password is not left in a file:

   ```sql
   CREATE LOGIN repo_scanner_local
       WITH PASSWORD = N'<password>',
            DEFAULT_DATABASE = RepoScanner,
            CHECK_POLICY = ON;
   GO
   USE RepoScanner;
   GO
   CREATE USER repo_scanner_local FOR LOGIN repo_scanner_local;
   ALTER ROLE db_datareader ADD MEMBER repo_scanner_local;
   ALTER ROLE db_datawriter ADD MEMBER repo_scanner_local;
   ```

5. **The password goes only in `.env`**, after `Password=` in `Scanner__Database__ConnectionString` (see [Run in a container](#run-in-a-container)). Never in the repository.
6. **Check it.** The first asks for the password and shows `repo_scanner_local   RepoScanner`; the second is refused with `CREATE TABLE permission denied`:

   ```powershell
   sqlcmd -S localhost,1433 -U repo_scanner_local -d RepoScanner -C -Q "SELECT SUSER_NAME(), DB_NAME()"
   ```

   ```powershell
   sqlcmd -S localhost,1433 -U repo_scanner_local -d RepoScanner -C -Q "CREATE TABLE dbo.ShouldFail (Id int)"
   ```

No Windows Firewall rule is needed: Docker Desktop passes `host.docker.internal` to this machine's own 127.0.0.1. To start again, for a new password, remove the login first, then repeat steps 3 to 6:

```sql
USE RepoScanner;
DROP USER repo_scanner_local;
DROP LOGIN repo_scanner_local;
```

### Change the model

After changing an entity in Core or a configuration in `src/RepoScanner.Data/Configurations/`, add a migration, read the generated file, then apply it:

```bash
dotnet ef migrations add <DescriptiveName> --project src/RepoScanner.Data --startup-project src/RepoScanner.Data
```

A change that removes or renames something follows expand/contract across releases (add the new, move the code over, remove the old later), never in one deployment ([ADR-006](docs/adr/ADR-006-migrations-applied-by-the-pipeline.md)).

### Who applies migrations

- **Locally:** you, with `dotnet ef database update` as above.
- **Azure:** only the pipeline, which generates an idempotent script with `dotnet ef migrations script --idempotent` and applies it before deploying ([ADR-006](docs/adr/ADR-006-migrations-applied-by-the-pipeline.md)).
- **Never the scanner:** it has no rights to change the schema ([ADR-008](docs/adr/ADR-008-two-privilege-levels.md)), so it never calls `Migrate()` or `EnsureCreated()`.

If `dotnet ef` says `Scanner__Database__ConnectionString` is not set, repeat step 2 of the setup and open a new terminal.

## Scan a local folder

The scanner can scan a folder on your machine instead of reading the queue. It runs the secret scanner, then the package scanner, then the code-pattern scanner, and saves the findings to the database in `Scanner:Database:ConnectionString`:

```bash
dotnet run --project src/RepoScanner.Scanner -- --path tests/fixtures/secrets-repo
```

```bash
dotnet run --project src/RepoScanner.Scanner -- --path tests/fixtures/packages-repo
```

```bash
dotnet run --project src/RepoScanner.Scanner -- --path tests/fixtures/patterns-repo
```

- The folder is saved in `Repositories` with the project name `Local`, and each scan as a row in `ScanRuns`, with one `ScannerResults` row per scanner.
- Each secret finding has a masked preview and a SHA-256 hash of the secret. The secret itself is never saved or logged ([ADR-023](docs/adr/ADR-023-secret-hash-is-an-unsalted-sha-256.md)).
- The package scanner reads the `.csproj` and `Directory.Packages.props` files, and reports each package version with a known vulnerability as a `PKG001` finding, with the advisory's severity and link. A floating version such as `12.0.*` is checked at the version a build would get. A version set centrally is reported once, on its `Directory.Packages.props` line ([Issue 5 design](docs/design/2026-09-27-nuget-vulnerability-scanner.md)).
- It also reports each version nuget.org marks deprecated, whose author has stopped maintaining it, as a `PKG002` finding: High for critical bugs, Low for legacy or other reasons, with the package nuget.org suggests instead, if any, and the version's nuget.org page as its link.
- The package scanner needs internet access to `api.nuget.org`: it downloads nuget.org's list of known vulnerabilities, and each package's metadata, once per run, and keeps them in memory only ([ADR-026](docs/adr/ADR-026-package-vulnerabilities-from-nuget-org-without-a-restore.md)). Nothing is written to the scanned folder.
- Running the same folder again updates the findings already stored rather than adding them twice: each keeps its row, with its line and last-seen time updated. A finding no longer found is marked `Resolved`, one found again after that is reopened, and a suppressed one stays suppressed until its date passes. Each change is recorded in `FindingEvents`, and the counts in the run's `ScanRuns` row ([Issue 4 design](docs/design/2026-09-27-findings-reconciliation.md)).
- The code-pattern scanner reads each `.cs` file with Roslyn, the C# compiler's parser, and reports six insecure patterns. Each finding's message says what to do instead and ends with the Microsoft guidance it follows ([ADR-018](docs/adr/ADR-018-code-pattern-rules-from-a-roslyn-syntax-walker-justified-by-analyser-ids.md), [Issue 6 design](docs/design/2026-09-28-code-pattern-scanner.md)):

  | Rule | Reports | Severity | Reference |
  |---|---|---|---|
  | `CP001` | SQL built by joining strings, given to `FromSqlRaw`, `ExecuteSqlRaw`, `SqlQueryRaw`, `new SqlCommand` or `CommandText` | High | CA2100 |
  | `CP002` | `BinaryFormatter`, `LosFormatter`, `NetDataContractSerializer`, `ObjectStateFormatter`, and Newtonsoft.Json's `TypeNameHandling` other than `None` | High | CA2300, CA2305, CA2310, CA2315, CA2326 |
  | `CP003` | A certificate callback that accepts every certificate, and `DangerousAcceptAnyServerCertificateValidator` | High | CA5359 |
  | `CP004` | MD5, DES and RC2; SHA1 and TripleDES | Medium | CA5351; CA5350 |
  | `CP005` | `AllowAnyOrigin()`, and `SetIsOriginAllowed` with a check that allows every origin | Medium | Enable CORS page |
  | `CP006` | A `catch` block with no statements and no comment | Low | related to CA1031 |

- A code-pattern finding keeps its identity when code above it moves or the file is reformatted: its fingerprint comes from its class, method and code, not its line ([ADR-024](docs/adr/ADR-024-fingerprint-per-scanner-without-line-numbers.md)). The two empty catches in two methods of one file are two findings.
- If one scanner fails, for example the package scanner because nuget.org cannot be reached, the other scanners carry on and their findings are saved. The run is then `PartiallyCompleted`: `ScanRuns.ErrorMessage` names the scanner that failed and why, and its `ScannerResults` row is `Failed`. A scanner that failed never resolves its own findings, so they keep their last status until it runs again ([ADR-025](docs/adr/ADR-025-auto-resolve-only-for-scanners-that-completed.md)). If every scanner fails, the run is `Failed`.
- Folders named `.git`, `bin`, `obj`, `node_modules`, `packages`, `.vs` and `TestResults`, files over 1 MB, binary files, symbolic links and files that cannot be opened are skipped.
- So is any folder below the scanned one that contains a `.reposcanner-exclude` file. RepoScanner's own test data has one, so its fake secrets and old packages are not reported when this repository is scanned.
- `tests/fixtures/secrets-repo`, `tests/fixtures/packages-repo` and `tests/fixtures/patterns-repo` are pretend repositories, for trying the scanners out: every secret in them is fake, the packages are old versions on purpose, and the insecure code is on purpose too.

## Scan from the queue locally

Without `--path`, the scanner does what it does in Azure: it takes scan requests from its queue one at a time, scans each repository, saves the findings, deletes the message, and exits when the queue is empty ([Issue 7 design](docs/design/2026-09-29-queue-consumer-and-execution-loop.md)). Locally the queue is in Azurite, outside Docker, as the storage account is outside the Container Apps environment in Azure. Each repository's latest commit is cloned from Azure DevOps into a new folder in `%TEMP%\reposcanner`, which is deleted after the scan ([Issue 8 design](docs/design/2026-10-01-repository-acquisition.md)).

### One-off setup

1. Install and start Azurite. `--location` keeps the queues and messages between restarts:

   ```bash
   npm install -g azurite
   ```

   ```bash
   azurite --location c:\azurite_data
   ```

2. Create a personal access token in BlacklightInternalsMo, where `victim-repo` is: **User settings**, then **Personal access tokens**, then **New Token**. Choose the organisation **BlacklightInternalsMo** (not "All accessible organizations"), the scope **Code: Read** and nothing else, and a short expiry.

3. Save it as a Windows user environment variable. Never put it in an appsettings file:

   ```bash
   setx Scanner__AzureDevOps__Pat "<the token>"
   ```

4. Open a new terminal. In VS Code, restart VS Code, since its new terminals copy its own environment; or copy the saved variable into the terminal you are in, without typing the token:

   ```powershell
   $env:Scanner__AzureDevOps__Pat = [Environment]::GetEnvironmentVariable("Scanner__AzureDevOps__Pat", "User")
   ```

The organisation, `https://dev.azure.com/BlacklightInternalsMo`, is in `appsettings.Development.json`, since it is not a secret.

The scanner never creates queues: in Azure it may only use them. The script below creates the four it needs, `scan-nightly`, `scan-ondemand`, `scan-nightly-poison` and `scan-ondemand-poison`, the first time; or create them in Storage Explorer.

### Add a scan request

With the script, which needs the Azure CLI:

```bash
.\scripts\enqueue-local.ps1 -RepositoryName victim-repo
```

- `-Queue scan-ondemand` puts it on the on-demand queue instead, which `dotnet run` reads when `Scanner__Queue__Name` is `scan-ondemand`.
- `-Malformed` sends `{ "hello": 1 }`, to try the poison queue.
- `-ProjectName` and `-DefaultBranch` default to `Kudos Wall` and `main`, where `victim-repo` is.
- The repository's Azure DevOps ID is made from its project and name, so the same repository always lands on the same `Repositories` row. `-RepositoryId <guid>` uses another, such as the real one, as the enqueuer will send it: `az repos show --org https://dev.azure.com/BlacklightInternalsMo --project "Kudos Wall" --repository victim-repo --query id -o tsv`. A different ID is a different `Repositories` row, so the same findings then appear under both.
- `-CorrelationId <guid>` sets the message's correlation ID, which its scan run keeps; a new one by default. `test-container.ps1` uses it to find its scan run.
- If PowerShell refuses to run scripts, run it as `powershell -ExecutionPolicy Bypass -File scripts\enqueue-local.ps1 -RepositoryName victim-repo`.

Or by hand in Storage Explorer: open the queue, **Add Message**, paste the message, and untick **Encode message body in Base64**, or the scanner cannot read it:

```json
{ "schemaVersion": 1, "correlationId": "eb7ff9f4-5926-4be3-bfde-a580db4deb5d", "repositoryId": "f4f1da55-68ba-41b2-a7ac-5f6a0d1d2059", "projectName": "Kudos Wall", "repositoryName": "victim-repo", "defaultBranch": "main" }
```

| Field | Rule |
|---|---|
| `schemaVersion` | `1` |
| `correlationId` | Any GUID. Saved as `ScanRuns.CorrelationId`, to follow one request through the logs |
| `repositoryId` | The repository's Azure DevOps GUID; any GUID locally. Keep the same one for the same repository |
| `projectName`, `defaultBranch` | Not empty, and the project not `.` or `..`. The branch is `main`, not `refs/heads/main` |
| `repositoryName` | The repository to clone. No `\`, `/` or `..` |

### Run it

```bash
dotnet run --project src/RepoScanner.Scanner
```

- Each message is scanned in turn, and deleted once its scan run is saved. The last line says why the run stopped and counts what it did: `Stopped taking messages from scan-nightly: queue empty. 1 scanned, 0 failed, 0 skipped, 0 poisoned`.
- **Each repository is cloned** into a new folder in `%TEMP%\reposcanner`, with only its latest commit, and the folder is deleted after the scan, whether it passed or failed. The log says `Cloned Kudos Wall/victim-repo main at <commit> in <n> ms; <n> MB free on C:\, <n> before`, and the commit is saved as the scan run's `CommitSha`.
- **A failed scan** is saved as a `Failed` scan run with the reason, and its message is left. It reappears after a minute locally (65 in Azure) and is tried again the next time you run. On its 4th pick-up it is moved to the lane's poison queue. A request for a repository that does not exist shows this, with git's reason: run `.\scripts\enqueue-local.ps1 -RepositoryName ghost`, then the scanner four times, a minute apart.
- **A rejected PAT**, wrong, expired or revoked, stops the run, since every other repository would fail the same way. The scan run is `Failed` with `Azure DevOps refused the token ...`, the log ends `Azure DevOps refused the token, scanner stopped: ...`, the process exits 1, and the message is left for when the token is fixed. No sign-in window opens.
- **A clone still going after 8 minutes** is stopped, and its scan run is `Failed` with `The clone took longer than 8 minutes and was stopped (Scanner:AzureDevOps:CloneTimeoutMinutes)`.
- **A message the scanner cannot read**, such as `-Malformed`, goes straight to the poison queue, with the reason in the log, and gets no scan run.
- **A repository another run is scanning**, with a `Running` scan run 20 minutes old or less, is skipped: the message is deleted and the log says which run is scanning it. A `Running` scan run older than that was left by a run that died: it is marked `Failed` as abandoned, and the repository is scanned ([ADR-027](docs/adr/ADR-027-skip-a-repository-being-scanned-take-over-an-abandoned-run.md)).
- A message in the poison queue is kept for 30 days. To try it again, fix the cause and move it back to its queue with Storage Explorer's **Move**.
- The run stops when the queue is empty, or when less than 15 minutes is left of its 50-minute budget, leaving the rest of the messages for the next run.

## Run in a container

The image is what Azure runs ([Issue 9 design](docs/design/2026-10-01-containerisation-and-local-docker-run.md)). The container holds only the scanner: it reaches Azurite and SQL Server on this machine through `host.docker.internal`, Docker Desktop's name for the machine it runs on, as the storage account and Azure SQL Database are outside the Container Apps environment in Azure. There is no docker-compose. Nothing starts the container when a message arrives, as the job's scale rule does in Azure: you run it.

### One-off setup

1. Install Docker Desktop and start it.
2. Start Azurite as in [Scan from the queue locally](#scan-from-the-queue-locally). It needs no extra flags: Docker Desktop passes `host.docker.internal` to this machine's own 127.0.0.1, where Azurite listens.
3. Set up SQL Server for the container, as in [Database and migrations, For the scanner container](#for-the-scanner-container): TCP/IP on port 1433, mixed-mode authentication and the `repo_scanner_local` login. A Linux container cannot use Windows authentication.
4. Copy `.env.example` to `.env`, which git ignores, and replace each `<...>`. One `NAME=value` per line, with no quotes: Docker keeps quotes as part of the value.

   ```bash
   copy .env.example .env
   ```

   | Setting | Value |
   |---|---|
   | `Scanner__Queue__Name` | `scan-nightly`, or `scan-ondemand` |
   | `Scanner__Queue__ConnectionString` | Azurite's full connection string, with `QueueEndpoint=http://host.docker.internal:10001/devstoreaccount1`. Replace `<azurite-account-key>` with the well-known key in the [Azurite documentation](https://learn.microsoft.com/azure/storage/common/storage-use-azurite#well-known-storage-account-and-key). `UseDevelopmentStorage=true` does not work here: it points at 127.0.0.1, which in the container is the container itself |
   | `Scanner__AzureDevOps__OrganizationUrl` | `https://dev.azure.com/BlacklightInternalsMo` |
   | `Scanner__AzureDevOps__Pat` | The same PAT as your `Scanner__AzureDevOps__Pat` user environment variable |
   | `Scanner__Database__ConnectionString` | `Server=host.docker.internal,1433` with the `repo_scanner_local` login and its password |

**Where settings live.** `dotnet run` and `dotnet ef` read your user environment variables; the container reads only `.env`. The two cannot share one place, because the database connection string differs: Windows authentication for `dotnet ef`, which creates and changes tables, and the read-and-write-only login for the container. **The PAT is in both, so a renewed PAT goes in both.** A `.env` with an old one exits 1 with `Azure DevOps refused the token`.

### Build and run

```bash
docker build -t reposcanner:v1 .
```

```bash
docker rm reposcanner-scan
```

```bash
docker run --name reposcanner-scan --env-file .env reposcanner:v1
```

- The scanner runs as it does in Azure: the `Production` environment, as the non-root user `app`, with every setting from `.env`.
- The stopped container keeps its name and stays, so its log stays in Docker Desktop (see [Logs](#logs)). A name can only be used once, so `docker rm` removes the last run's container first; the first time, Docker says there is no such container, which is harmless.
- One container takes every message on the queue, one after another, then stops. Each `docker run` is a new container.
- Its log is JSON, as in Azure. Add `Scanner__Logging__Format=Text` to `.env` to read it as text.
- The exit code is the scanner's (see [Exit codes](#exit-codes)).
- With no Development settings, a failed scan's message reappears after 65 minutes, not 1. Add `Scanner__Execution__VisibilityTimeoutMinutes=1` to `.env` to change that.

### Check it end to end

```bash
.\scripts\test-container.ps1
```

It stops at the first check that fails, says what to start or fix, and exits 1; it exits 0 when all 13 pass:

- **`.env`:** every line is `NAME=value`, with no quotes, no space at either end of a value, no `<...>` placeholder left, a queue name of `scan-nightly` or `scan-ondemand`, and all five settings there. A problem is named by its line and setting, never its value.
- **The image:** it builds as `reposcanner:v1`, runs as a non-root user, has git 2.31 or later, no .NET SDK and no Development settings, and every base image in the Dockerfile has a tag other than `latest`.
- **The connections:** a container reaches SQL Server on `host.docker.internal:1433` and Azurite on `host.docker.internal:10001`.
- **A real scan:** it sends a request for `victim-repo`, with its own correlation ID, to the queue `.env` names, runs the container, and checks that scan run is `Completed` or `PartiallyCompleted` with a commit ID, and that the container's log is JSON, one object per line, with a summary line carrying that correlation ID. Messages already on that queue are scanned too.
- **An empty queue:** a second run exits 0 and saves no scan run.

`-RepositoryName`, `-Tag`, `-SqlServer` and `-EnvFile` change what it uses. It needs Docker Desktop, Azurite and SQL Server running, the Azure CLI (it sends the request with `enqueue-local.ps1`) and sqlcmd, and reads the scan runs with Windows authentication. If PowerShell refuses to run scripts, run it as `powershell -ExecutionPolicy Bypass -File scripts\test-container.ps1`.

### Image size and caching

Measured on 2026-10-01 with Docker Desktop 29. Content size is what a registry stores and a machine downloads; disk usage is the image unpacked.

| Image | Content size | Disk usage |
|---|---|---|
| `reposcanner:v1` | 141 MB | 543 MB |
| `mcr.microsoft.com/dotnet/runtime:10.0`, its base | 83.2 MB | 300 MB |
| `mcr.microsoft.com/dotnet/sdk:10.0`, used only to build | 351 MB | 1.3 GB |

On top of the runtime, the image adds git and its packages (79 MB unpacked) and the published scanner (106 MB unpacked, most of it Roslyn, EF Core and the Azure SDK). Shipping the runtime rather than the SDK keeps it at less than half the SDK's size.

| Build | Time |
|---|---|
| With no cache (`docker build --no-cache`) | 27 s: restoring packages 14 s and installing git 17 s, side by side, then publishing 6 s |
| After changing one `.cs` file | 13 s: only `COPY src/`, the publish and the final copy run |
| With nothing changed | 2 s |

The project files are copied and restored before the code, so the restore step stays `CACHED` until something copied before it changes: a `.csproj`, `Directory.Packages.props` (the package versions), `Directory.Build.props`, `global.json` or `.editorconfig`. A change to the code alone never re-runs it. Installing git is `CACHED` until the runtime image changes.

## Logs

The scanner writes its log to the console. `dotnet run`, which uses `Development`, prints readable text; everywhere else, the container and Azure included, each line is one JSON object, because Container Apps sends each line to Log Analytics, which can then search any field ([ADR-028](docs/adr/ADR-028-json-log-lines-on-stdout-outside-development.md), [Issue 10 design](docs/design/2026-10-02-observability-and-run-summary.md)). `Scanner:Logging:Format` (`Text` or `Json`) overrides that.

### What one scan logs

One run takes every message on its queue, one after another, so its log holds many repositories. For each one, in order:

1. `Message ... for Kudos Wall/victim-repo, correlation ..., attempt 1`
2. `Cloned Kudos Wall/victim-repo main at <commit> in ... ms`
3. For each scanner: its own line, saying how many files it read and what it skipped, then one line in the same shape for all three, such as `Package scanner Succeeded in 812 ms: 4 findings`. A scanner that failed has a warning here, with its error.
4. One line per new, reopened and resolved finding.
5. One summary line, last: `Scan run 7 Completed (Nightly) for Kudos Wall/victim-repo at <commit> in 2748 ms: 14 found, 0 new, 0 reopened, 0 resolved, 0 suppressions expired. Scanners: {...}`. It is a warning ending `Error: ...` when the run is `PartiallyCompleted` or `Failed`, whatever made it fail.

Every one of these lines carries the message's `CorrelationId`, `ProjectName` and `RepositoryName`, so one search finds a scan's lines among the rest. The correlation ID comes from the message: the enqueuer makes one per message, the dashboard one per "Scan now", and `enqueue-local.ps1` one per request; `--path` makes its own. It is also saved as `ScanRuns.CorrelationId`. Lines logged before a message is read, or after the run stops, have none: the run's first and last lines, a message the scanner cannot read, and the start-up logger's fatal lines, such as a rejected PAT.

In JSON, `@t` is the time the line was logged, `@m` the finished message, with string values in quotes, `@l` the level (left out for Information), `@x` any exception with its stack trace, and every value in the message is also a field of its own.

### Reading a container's log locally

There is no Log Analytics on this machine. Run the container with a name, as in [Build and run](#build-and-run), then in Docker Desktop open **Containers**, the `reposcanner-scan` container, then **Logs**. Its search box finds a correlation ID; `docker logs reposcanner-scan` prints the same log.

### Searching in Log Analytics

Each line is in the `Log` column of `ContainerAppConsoleLogs` as text, so a search first unpacks it with `parse_json`.

One scan, every line:

```kusto
ContainerAppConsoleLogs
| extend e = parse_json(Log)
| where tostring(e.CorrelationId) == "77438d01-1e66-47ae-a0ed-bd5c86c6f446"
| project Time = todatetime(e["@t"]), Level = coalesce(tostring(e["@l"]), "Information"), Message = tostring(e["@m"])
| order by Time asc
```

Scans that did not complete, in the last day:

```kusto
ContainerAppConsoleLogs
| where TimeGenerated > ago(1d)
| extend e = parse_json(Log)
| where isnotempty(tostring(e.ScanRunStatus)) and tostring(e.ScanRunStatus) != "Completed"
| project TimeGenerated, Repository = tostring(e.RepositoryName), Status = tostring(e.ScanRunStatus), Error = tostring(e.ErrorMessage), CorrelationId = tostring(e.CorrelationId)
```

Changing the last lines gives other searches. One repository's lines in the last day: `where TimeGenerated > ago(1d) and tostring(e.RepositoryName) == "Kudos-Wall"`. Slow scans: `where toint(e.DurationMs) > 600000`. Each scanner's average time: `summarize avg(toint(e.ScannerDurationMs)) by tostring(e.ScannerType)`.

### Alerts

The pipeline creates the alert rules, how often they run and who they tell (Issue 12). They use these two searches, and tests fail if either line they rely on changes.

A message moved to a poison queue. Only the two poison lines have `PoisonQueueName`, so the search does not depend on their wording:

```kusto
ContainerAppConsoleLogs
| extend e = parse_json(Log)
| where tostring(e["@l"]) == "Warning" and isnotempty(tostring(e.PoisonQueueName))
| project TimeGenerated, ContainerGroupName, Message = tostring(e["@m"])
```

A rejected PAT, which stops the run:

```kusto
ContainerAppConsoleLogs
| extend e = parse_json(Log)
| where tostring(e["@l"]) == "Fatal" and tostring(e["@m"]) startswith "Azure DevOps refused the token"
| project TimeGenerated, ContainerGroupName, Message = tostring(e["@m"])
```

## Known limitations

The queue loop:

- In Azure, a scan that crashes is tried again only when its message reappears, about 65 minutes later. For "Scan now", the dashboard shows the run as `Failed` on the next pick-up, and the user can click again.
- A skipped request is not kept. If the scan it gave way to then fails, the repository waits for the next night or another "Scan now".
- Two runs that check the same repository at the same instant can both scan it. Its findings are not duplicated, but there are two scan runs ([ADR-027](docs/adr/ADR-027-skip-a-repository-being-scanned-take-over-an-abandoned-run.md)).
- A message the scanner cannot read shows only in the log and the poison queue, not in the dashboard.
- A repository renamed since its message was sent is looked for under its old name, so its clone fails; the next nightly message has the new one.

Cloning:

- **Only the latest commit of the default branch is scanned.** A secret committed and later removed stays in history and is not found, and other branches are not scanned ([ADR-016](docs/adr/ADR-016-shallow-single-branch-clone-with-the-credential-in-a-header.md)).
- Files kept in Git LFS are scanned as their small pointer files: their content is never downloaded, since LFS would fetch it from whatever server the repository names, and the PAT is sent only to the organisation.
- One organisation per scanner deployment, since a PAT belongs to one.
- The PAT acts as the person who created it: it sees only their repositories, stops working if their account is removed, and expires within a year, so it needs renewing.
- While git runs, the PAT is in its process's environment, which another program running as the same user can read.
- A rejected PAT is recognised from git's English error text, so a git that prints another language or other wording gives an ordinary failed scan instead.
- If the scanner process is killed outright, its folder in `%TEMP%\reposcanner` is left behind. In Azure the replica's disk goes with it; locally it stays until you delete it.

The package scanner:

- **Checks only the packages the project files list directly.** Packages they depend on (transitive dependencies) are known only after a `dotnet restore`, which the scanner does not run ([ADR-026](docs/adr/ADR-026-package-vulnerabilities-from-nuget-org-without-a-restore.md)). A vulnerable package brought in by another package is not reported.
- Does not read `Directory.Build.props`, `Directory.Build.targets` or imported files, ignores `Condition` attributes, and skips a version written as an MSBuild property, such as `$(JsonVersion)`.
- Checks a package from a private feed only against nuget.org's advisories for a package with the same ID, and skips its floating versions, because nuget.org does not know it.
- Checks a plain version as written. If that version does not exist, a build would use the next one up, which may differ.
- Resolves a floating version from every version nuget.org lists, including unlisted ones that a build would skip.
- Opens a new finding, and resolves the old one, when nuget.org publishes a version that a floating reference then picks; a suppression does not follow it ([ADR-024](docs/adr/ADR-024-fingerprint-per-scanner-without-line-numbers.md)).
- Reports a version set in `Directory.Packages.props` on that file, without naming the projects that use it.
- Leaves package findings as they were while nuget.org cannot be reached.
- Checks deprecation for the exact version in use: nuget.org marks versions one by one, so a package whose author deprecated only older versions gives no `PKG002` for a newer one.
- Names the alternative package nuget.org suggests, but does not check its version range, and does not store the author's own deprecation note; the version's nuget.org page shows it.
- Reads deprecation from nuget.org only, so a package from a private feed is never reported as deprecated.

The code-pattern scanner:

- **Looks at the shape of the code, not what it means** ([ADR-018](docs/adr/ADR-018-code-pattern-rules-from-a-roslyn-syntax-walker-justified-by-analyser-ids.md)). A pattern behind a variable or a helper method is missed, such as SQL built on one line and passed to `FromSqlRaw` on the next, or a certificate callback set to a named method.
- Matches names as written, without knowing their types: a class of the repository's own called `MD5` or `SqlCommand` is treated like the real one, and `"SELECT " + SomeConstant` is flagged although the constant is safe. Such a finding is suppressed with a reason.
- Does not see a type created with `new(...)` without its name, or a certificate callback passed straight to a constructor, such as `new SslStream(stream, false, (a, b, c, d) => true)`.
- Reads files as a plain build with no symbols defined, so code under `#if DEBUG` is not scanned.
- Does not read `.razor` or `.cshtml` files; their `.razor.cs` code-behind files are read.
- Reports weak hashing wherever it is used, including checksums, where it is harmless.
- Skips, with a warning, a file whose parentheses and angle brackets nest more than 200 deep, and one the parser gives up on, because Roslyn's parser can crash the process on very deeply nested code.
- Opens a new finding, and resolves the old one, when the method or class around it is renamed or its code changes; a suppression does not follow it ([ADR-024](docs/adr/ADR-024-fingerprint-per-scanner-without-line-numbers.md)).

The container:

- The base images are tagged `10.0`, so two builds a month apart can have different .NET 10 patches and Ubuntu packages, including git.
- `host.docker.internal` comes with Docker Desktop. A Linux machine, such as a pipeline agent, needs `--add-host=host.docker.internal:host-gateway`.
- The PAT is in two places locally, your user environment variable and `.env`, and both need renewing.
- `.env` holds the PAT and the SQL password in plain text. It is git-ignored and kept out of the image, but anyone who can read the repository folder can read it.
- The image is built for this machine's platform, linux/amd64, which is what Container Apps runs.
- Nothing starts the container when a message arrives; you run it. `--path` is not supported in the container.

Logs:

- Lines logged before a message is read, or after the loop stops, have no correlation ID or repository.
- The file walker logs its `Found ... files` line once per scanner, so three times per scan, because each scanner walks the files itself. It is left as it is: each line says what that walk skipped.
- Text lines, in `Development` or with `Scanner__Logging__Format=Text`, can only be searched by their words.
- The start-up logger reads `Scanner:Logging:Format` from environment variables and the command line only, not from appsettings files.
- In JSON, Information lines have no `@l`, and `@m` puts string values in quotes.
- The searches are checked against Log Analytics only once Issue 12 deploys the job; until then, tests check the JSON lines they rely on.
- Field names are part of the searches and alerts: renaming one breaks any search that uses it.

## Exit codes

The Container Apps Job reads the exit code to decide whether an execution succeeded.

| Code | Meaning |
|---|---|
| `0` | Success, or nothing to do. Also a run in which some scanners failed: it is saved as `PartiallyCompleted` and logged as a warning. In queue mode, also a run in which some repositories failed: each is saved as a `Failed` scan run and its message is tried again |
| `1` | Unexpected failure during the run, logged with the full exception. With `--path`, also when every scanner failed. In queue mode, also when the queue or the database cannot be reached, when Azure DevOps refuses the PAT, or when the run is stopped (Ctrl+C, or Azure stopping the job) |
| `2` | Invalid configuration, a `--path` that does not name an existing folder, or a queue or poison queue that does not exist: logged as one line saying what is wrong, listing every setting that is wrong when there are several |

In a container, the container's exit code is the scanner's.

To see exit code 2 locally, run without the Development settings:

```bash
dotnet run --project src/RepoScanner.Scanner --no-launch-profile
```

## Repository layout

```
global.json               pins the .NET SDK version
Directory.Build.props     build settings shared by every project
Directory.Packages.props  every NuGet package version, set once (Central Package Management)
.editorconfig             code style rules, enforced during build
Dockerfile                the scanner image: built with the .NET SDK, shipped on the .NET runtime with git
.dockerignore             what the image build may read: only the build files and src/
.env.example              the container's settings, copied to the git-ignored .env and filled in
.config/
  dotnet-tools.json       pins the dotnet ef tool version
RepoScanner.sln           solution file
scripts/
  enqueue-local.ps1       puts a scan request on a queue in local Azurite, creating the queues first if needed
  test-container.ps1      builds the scanner image and checks it, its connections and a real scan end to end
docs/
  adr/                    architecture decision records (index in docs/adr/README.md)
  design/                 design documents, one per issue
  images/                 diagrams used in the documentation
  superpowers/plans/      implementation plans, one per issue (they quote the fixtures' fake secrets, so the folder is excluded from scans)
src/
  RepoScanner.Core/       domain types and contracts shared by every project (no dependencies)
  RepoScanner.Data/       EF Core database access (DbContext, Fluent API configurations, migrations), shared with the dashboard
  RepoScanner.Scanner/    the console app
tests/
  RepoScanner.Core.Tests/     NUnit tests for Core
  RepoScanner.Data.Tests/     NUnit tests for Data
  RepoScanner.Scanner.Tests/  NUnit tests for Scanner
  fixtures/                   pretend repositories for the scanners' tests (every secret in them is fake, every package old and every insecure pattern there on purpose)
```

## Conventions

The reasons behind the design are recorded as [architecture decision records](docs/adr/README.md).

- Work happens on `feature/<work-item-id>-<short-name>` branches created from `develop`.
- `develop` and `main` only change through pull requests linked to an Azure Boards work item.
- Implicit usings are off: every file lists its own `using` directives.
- No top-level statements: the entry point is `Program.Main`.
