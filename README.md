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
- Git
- SQL Server installed on your machine (Express, Developer or a full edition), reached with Windows authentication. Only needed to create and use the local database; the tests do not need it
- SQL Server Management Studio (optional), to look at the tables

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
| `Scanner:Queue:Name` | Yes | | `scan-nightly` or `scan-ondemand`. Lowercase letters, numbers and hyphens, 3 to 63 characters |
| `Scanner:Queue:ConnectionString` | One of these two | | Local only: `UseDevelopmentStorage=true` (Azurite) |
| `Scanner:Queue:ServiceUri` | One of these two | | Azure only, used with managed identity |
| `Scanner:Queue:PoisonQueueName` | | `<Name>-poison` | Where failing messages are moved. One per lane, e.g. `scan-nightly-poison` (ADR-013) |
| `Scanner:Queue:MaxDequeueCount` | | `3` | Attempts before a message is moved to the poison queue (ADR-013) |
| `Scanner:Database:ConnectionString` | Yes | | The findings database. Locally, the `Scanner__Database__ConnectionString` user environment variable (see [Database and migrations](#database-and-migrations)); never in an appsettings file (ADR-007) |

As environment variables, `:` becomes `__`, for example:

```powershell
$env:Scanner__Queue__Name = "scan-ondemand"
```

Further settings (Azure DevOps, execution limits) are added with the features that use them. The scanner and the `dotnet ef` tool read the same database connection string: see [Database and migrations](#database-and-migrations).

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

The scanner can scan a folder on your machine instead of reading the queue. It runs the secret scanner, then the package scanner, and saves the findings to the database in `Scanner:Database:ConnectionString`:

```bash
dotnet run --project src/RepoScanner.Scanner -- --path tests/fixtures/secrets-repo
```

```bash
dotnet run --project src/RepoScanner.Scanner -- --path tests/fixtures/packages-repo
```

- The folder is saved in `Repositories` with the project name `Local`, and each scan as a row in `ScanRuns`, with one `ScannerResults` row per scanner.
- Each secret finding has a masked preview and a SHA-256 hash of the secret. The secret itself is never saved or logged ([ADR-023](docs/adr/ADR-023-secret-hash-is-an-unsalted-sha-256.md)).
- The package scanner reads the `.csproj` and `Directory.Packages.props` files, and reports each package version with a known vulnerability as a `PKG001` finding, with the advisory's severity and link. A floating version such as `12.0.*` is checked at the version a build would get. A version set centrally is reported once, on its `Directory.Packages.props` line ([Issue 5 design](docs/design/2026-09-27-nuget-vulnerability-scanner.md)).
- The package scanner needs internet access to `api.nuget.org`: it downloads nuget.org's list of known vulnerabilities once per run, and keeps it in memory only ([ADR-026](docs/adr/ADR-026-package-vulnerabilities-from-nuget-org-without-a-restore.md)). Nothing is written to the scanned folder.
- Running the same folder again updates the findings already stored rather than adding them twice: each keeps its row, with its line and last-seen time updated. A finding no longer found is marked `Resolved`, one found again after that is reopened, and a suppressed one stays suppressed until its date passes. Each change is recorded in `FindingEvents`, and the counts in the run's `ScanRuns` row ([Issue 4 design](docs/design/2026-09-27-findings-reconciliation.md)).
- If one scanner fails, for example the package scanner because nuget.org cannot be reached, the other scanners carry on and their findings are saved. The run is then `PartiallyCompleted`: `ScanRuns.ErrorMessage` names the scanner that failed and why, and its `ScannerResults` row is `Failed`. A scanner that failed never resolves its own findings, so they keep their last status until it runs again ([ADR-025](docs/adr/ADR-025-auto-resolve-only-for-scanners-that-completed.md)). If every scanner fails, the run is `Failed`.
- Folders named `.git`, `bin`, `obj`, `node_modules`, `packages`, `.vs` and `TestResults`, files over 1 MB, binary files, symbolic links and files that cannot be opened are skipped.
- So is any folder below the scanned one that contains a `.reposcanner-exclude` file. RepoScanner's own test data has one, so its fake secrets and old packages are not reported when this repository is scanned.
- `tests/fixtures/secrets-repo` and `tests/fixtures/packages-repo` are pretend repositories, for trying the scanners out: every secret in them is fake, and the packages are old versions on purpose.

## Known limitations

The package scanner:

- **Checks only the packages the project files list directly.** Packages they depend on (transitive dependencies) are known only after a `dotnet restore`, which the scanner does not run ([ADR-026](docs/adr/ADR-026-package-vulnerabilities-from-nuget-org-without-a-restore.md)). A vulnerable package brought in by another package is not reported.
- Does not read `Directory.Build.props`, `Directory.Build.targets` or imported files, ignores `Condition` attributes, and skips a version written as an MSBuild property, such as `$(JsonVersion)`.
- Checks a package from a private feed only against nuget.org's advisories for a package with the same ID, and skips its floating versions, because nuget.org does not know it.
- Checks a plain version as written. If that version does not exist, a build would use the next one up, which may differ.
- Resolves a floating version from every version nuget.org lists, including unlisted ones that a build would skip.
- Opens a new finding, and resolves the old one, when nuget.org publishes a version that a floating reference then picks; a suppression does not follow it ([ADR-024](docs/adr/ADR-024-fingerprint-per-scanner-without-line-numbers.md)).
- Reports a version set in `Directory.Packages.props` on that file, without naming the projects that use it.
- Leaves package findings as they were while nuget.org cannot be reached.

## Exit codes

The Container Apps Job reads the exit code to decide whether an execution succeeded.

| Code | Meaning |
|---|---|
| `0` | Success, or nothing to do. Also a run in which some scanners failed: it is saved as `PartiallyCompleted` and logged as a warning |
| `1` | Unexpected failure during the run, or every scanner failed (logged with the full exception) |
| `2` | Invalid configuration, or a `--path` that does not name an existing folder: the scanner did not start (logged as one line saying what is wrong) |

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
.config/
  dotnet-tools.json       pins the dotnet ef tool version
RepoScanner.sln           solution file
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
  fixtures/                   pretend repositories for the scanners' tests (every secret in them is fake, every package old on purpose)
```

## Conventions

The reasons behind the design are recorded as [architecture decision records](docs/adr/README.md).

- Work happens on `feature/<work-item-id>-<short-name>` branches created from `develop`.
- `develop` and `main` only change through pull requests linked to an Azure Boards work item.
- Implicit usings are off: every file lists its own `using` directives.
- No top-level statements: the entry point is `Program.Main`.
