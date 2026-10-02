# Issue 9: Containerisation and local docker run, implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The scanner ships as the image `reposcanner:v1`, built from the .NET runtime with git and run as a non-root user, and `docker run --rm --env-file .env reposcanner:v1` scans a queue message from Azurite and saves its findings to the local SQL Server, both outside the container.

**Architecture:** A two-stage `Dockerfile` compiles with `sdk:10.0` and ships `runtime:10.0` with git and the published app. `.dockerignore` lets in only what the build needs. Every container setting comes from the git-ignored `.env`; the container reaches Azurite and SQL Server through `host.docker.internal`. `scripts/test-container.ps1` checks the image, the connections and a real scan end to end. No production C# changes.

**Tech Stack:** Docker Desktop 29 (Linux containers), `mcr.microsoft.com/dotnet/sdk:10.0` and `runtime:10.0` (Ubuntu 24.04), git 2.43 from apt, `busybox:1.37` for connection checks, Windows PowerShell 5.1, `sqlcmd`, Azurite, the Azure CLI. No new packages.

**Spec:** [docs/design/2026-10-01-containerisation-and-local-docker-run.md](../../design/2026-10-01-containerisation-and-local-docker-run.md). Read it before starting; this plan argues from it. The design itself was committed as `ebba9a7` (#56).

## Global Constraints

- Branch: `feature/56-containerisation-and-local-docker-run` (already created from `develop`).
- **Commits:** one per task, only after the user has reviewed that task's diff. The message starts `This commit` and ends with the task number, for example `#57`. It never mentions Claude, Opus or any AI tool: no `Co-Authored-By` line.
- **Board:** set the task to Doing when its plan task starts and to Done when its commit is made, with `az boards work-item update --org https://dev.azure.com/Blacklight-Internal --id <id> --state <Doing|Done>`. Issue #56 stays Doing until its pull request is merged. Push and open a pull request only when the user asks.
- **Order:** #57, #58, #59, #60, then #70. The spec puts #70 fourth; the user moved it last on 2026-10-01 because they have more to do on it. #70 stays Doing until Task 5.
- **Secrets:** `.env` is never staged, printed, echoed or read back into the conversation. Before every commit, `git status --short` must not list `.env`. The PAT and the SQL password are read from where they are and written straight to `.env`, never through a command-line argument or a log line. The test script reports a bad `.env` line by its line number and setting name, never its value.
- Exact values from the spec:
  - Image tag `reposcanner:v1`; base images `mcr.microsoft.com/dotnet/sdk:10.0` and `mcr.microsoft.com/dotnet/runtime:10.0`; connection checks from `busybox:1.37`. No `latest` anywhere.
  - The user is `$APP_UID`, `app`, number 1654. Git must be 2.31 or later.
  - Run command: `docker run --rm --env-file .env reposcanner:v1`.
  - `host.docker.internal`: port `10001` for Azurite, `1433` for SQL Server.
  - The five `.env` names, in this order: `Scanner__Queue__Name`, `Scanner__Queue__ConnectionString`, `Scanner__AzureDevOps__OrganizationUrl`, `Scanner__AzureDevOps__Pat`, `Scanner__Database__ConnectionString`, with the values in the spec's `.env` table.
- **Windows PowerShell 5.1** is the only PowerShell on the developer's machine (no `pwsh`). Scripts use no `&&`, `||`, `??` or `?.`. A native command is never run with `2>&1` while `$ErrorActionPreference` is `Stop`, because 5.1 turns each standard-error line into a terminating error and `docker` writes its progress there; results come from `$LASTEXITCODE`. Scripts follow `scripts/enqueue-local.ps1`: comment-based help, `[CmdletBinding()]`, `$ErrorActionPreference = 'Stop'`, and a comment giving the reason for every non-obvious line.
- **Shells:** commands with `printf`, `grep`, `ls` or `echo "exit $?"` are for Git Bash, where `$?` is the last exit code; commands with `Get-Content`, `Measure-Command` or `.\scripts\...` are for Windows PowerShell.
- No em dash (U+2014) in any file. British English.
- `dotnet build RepoScanner.sln` has zero warnings and `dotnet test RepoScanner.sln` passes with the coverage gate at the end of every task. Filtered test runs add `-p:CollectCoverage=false`.

## Review Focus

Inputs the spec does not mention, most likely to bite a person first. Each has a check in the task named.

1. **A repository path with spaces, and a script started from another folder.** This repository is in `C:\Code Security Analysis EPA\RepoScanner`: every path the script hands to `docker` and `sqlcmd` is quoted and found from `$PSScriptRoot`, never the current folder. Task 3: the run started from `scripts\`.
2. **A `.env` value wrapped in quotes**, as many `.env` tools allow: Docker keeps the quotes as part of the value, so the queue name or sign-in fails with a confusing error. The script refuses it before building, naming the line. Task 3: the quoted-value check.
3. **A `.env` copied from `.env.example` and not filled in**, or missing a setting: the script names the setting still holding a `<...>` placeholder, or missing, before building. Task 3: the run with `-EnvFile .env.example`.
4. **Docker Desktop, Azurite or SQL Server not running**: the script says which one and how to start it, instead of a raw `docker` or `nc` error. Task 3: the run with Azurite stopped.
5. **Real secrets pasted into `.env.example`** by someone filling in the wrong file: the self-scan test fails. Task 2: `ScanAsync_OnTheEnvExampleFile_FindsNothing`.

---

### Task 1: The image (#57)

**Files:**
- Create: `Dockerfile`, `.dockerignore`
- Modify: `src/RepoScanner.Scanner/RepoScanner.Scanner.csproj` (the `appsettings*.json` item group)

**Interfaces:**
- Consumes: nothing new.
- Produces: the image `reposcanner:v1`, entrypoint `dotnet RepoScanner.Scanner.dll` in `/app`, running as user 1654. Later tasks build it with `docker build -t reposcanner:v1 .` from the repository root.

- [ ] **Step 1: Set #57 to Doing**

- [ ] **Step 2: See the build fail**

Run from the repository root: `docker build -t reposcanner:v1 .`
Expected: fails with `failed to read dockerfile` (no `Dockerfile`).

- [ ] **Step 3: Write `.dockerignore` and `Dockerfile`**

Exactly as in the spec's **The image** section, comments included.

- [ ] **Step 4: Check what reaches the build**

Run: `printf 'FROM busybox:1.37\nCOPY . /ctx\nRUN ls -A /ctx /ctx/src\n' | docker build --no-cache --progress=plain -f - .`
Expected: `/ctx` lists only `.dockerignore`-allowed entries: `.editorconfig`, `Directory.Build.props`, `Directory.Packages.props`, `global.json`, `src`; `/ctx/src` lists `RepoScanner.Core`, `RepoScanner.Data`, `RepoScanner.Scanner`. No `.env`, `.git`, `tests` or `docs`. The image is not tagged and needs no clean-up beyond `docker image prune`.

- [ ] **Step 5: Leave `appsettings.Development.json` out of the published app**

Add the spec's second `<None Update="appsettings.Development.json" CopyToPublishDirectory="Never" />` line under the existing one, with a comment: published images run as Production with every setting from environment variables (Issue 9 design, decision 4).

Run: `dotnet publish src/RepoScanner.Scanner -c Release -o "$TEMP/rs-publish"`, then `ls "$TEMP/rs-publish" | grep -c appsettings`, then `ls src/RepoScanner.Scanner/bin/Debug/net10.0 | grep appsettings`.
Expected: `0` for the publish folder; the Debug build folder still has `appsettings.Development.json`, so `dotnet run` is unchanged. Delete `$TEMP/rs-publish` afterwards.

- [ ] **Step 6: Build the image**

Run: `docker build -t reposcanner:v1 .`
Expected: succeeds, with no build-check warnings from Docker and no warnings from `dotnet publish` (any would fail it: `TreatWarningsAsErrors`).

- [ ] **Step 7: Check the image by hand**

| Command | Expected |
|---|---|
| `docker run --rm --entrypoint id reposcanner:v1 -u` | `1654` |
| `docker run --rm --entrypoint git reposcanner:v1 --version` | `git version 2.43.0` or later |
| `docker run --rm --entrypoint dotnet reposcanner:v1 --list-sdks` | no output |
| `docker run --rm --entrypoint sh reposcanner:v1 -c "test ! -e /app/appsettings.Development.json && echo absent"` | `absent` |
| `docker run --rm reposcanner:v1; echo "exit $?"` | one line starting `Invalid configuration, scanner not started:` naming `Scanner:Database:ConnectionString is required`, then `exit 2`. This proves the image starts, runs as Production and reads no Development settings |
| `grep -n "^FROM" Dockerfile` | two lines, `sdk:10.0 AS build` and `runtime:10.0 AS final` |

- [ ] **Step 8: Full build and tests**

`dotnet build RepoScanner.sln` (zero warnings), then `dotnet test RepoScanner.sln`. Expected: all pass, coverage gate met.

- [ ] **Step 9: Stop for review, then commit and set #57 to Done**

```bash
git add Dockerfile .dockerignore src/RepoScanner.Scanner/RepoScanner.Scanner.csproj
git commit -m "This commit adds the scanner image: a Dockerfile that restores and publishes with sdk:10.0, restoring before copying the code so a code-only change reuses the restore step, and ships runtime:10.0 with git from apt and the published app, running as the image's non-root user app and starting dotnet RepoScanner.Scanner.dll in exec form; a .dockerignore that lets in only global.json, the shared build files, .editorconfig and src/ without bin and obj; and leaves appsettings.Development.json out of the published app, so the image reads every setting from environment variables #57"
```

---

### Task 2: Running it with .env (#58)

**Files:**
- Create: `.env.example`
- Modify: `.env` (git-ignored, never staged), `tests/RepoScanner.Scanner.Tests/Secrets/SelfScanTests.cs`, `docs/adr/ADR-017-everything-runs-locally-before-azure.md`, `docs/adr/README.md`, `README.md`

**Interfaces:**
- Consumes: `reposcanner:v1` (Task 1); `RepositoryRoot.FullPath`, `TempFolder` and `AllSecretRules.Create()` from the test project.
- Produces: a complete `.env` with the five settings, used by Tasks 3 and 4; `.env.example` with the same five lines; the README section `## Run in a container`, which Tasks 3 and 4 add to.

- [ ] **Step 1: Set #58 to Doing**

- [ ] **Step 2: Write the failing test**

In `SelfScanTests`, beside the existing self-scan:

```csharp
// .env.example is committed and sits beside the real .env, so someone filling in the wrong file would commit
// real secrets. Copied into a folder of its own, so the real .env is never scanned or printed.
[Test]
public async Task ScanAsync_OnTheEnvExampleFile_FindsNothing()
// Copy RepositoryRoot.FullPath/.env.example into a TempFolder, scan it with every secret rule;
// findings as "RuleId FilePath:LineNumber" Is.Empty
```

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~Secrets.SelfScanTests" -p:CollectCoverage=false`
Expected: the new test fails with `FileNotFoundException` for `.env.example`.

- [ ] **Step 3: Write `.env.example`**

Three comment lines: it holds the scanner container's settings for `docker run --env-file .env` (README: Run in a container); copy it to `.env`, which git ignores, and replace each `<...>`; one `NAME=value` per line with no quotes, because Docker keeps quotes as part of the value. Then the five lines from the spec's `.env` table, in the Global Constraints order, with `<azurite-account-key>`, `<pat>` and `<password>` as the placeholders.

Run the step 2 command. Expected: all pass.

- [ ] **Step 4: Complete `.env`**

`.env` already has a comment line and `Scanner__Database__ConnectionString` (written in #70). Rewrite it in PowerShell, so no value is printed:

- Keep the existing `Scanner__Database__ConnectionString` line as it is.
- `Scanner__AzureDevOps__Pat` from `[Environment]::GetEnvironmentVariable('Scanner__AzureDevOps__Pat', 'User')`; stop with an error if it is empty.
- `Scanner__Queue__ConnectionString` with Azurite's well-known `devstoreaccount1` key, from [the Azurite documentation](https://learn.microsoft.com/azure/storage/common/storage-use-azurite#well-known-storage-account-and-key), and `QueueEndpoint=http://host.docker.internal:10001/devstoreaccount1;`.
- `Scanner__Queue__Name` and `Scanner__AzureDevOps__OrganizationUrl` as in the spec.
- The same three comment lines as `.env.example`, then the five lines in order. Written with `[System.IO.File]::WriteAllText` and `New-Object System.Text.UTF8Encoding $false`: no byte order mark, which Windows PowerShell's `Set-Content -Encoding UTF8` would add.

Then check, printing names only:

```powershell
Get-Content .env | Where-Object { $_ -match '^[A-Za-z_]' } | ForEach-Object { ($_ -split '=', 2)[0] }
git check-ignore .env
```

Expected: the five names in order, then `.env`.

- [ ] **Step 5: Run the container against an empty queue**

With Azurite running. Run: `docker run --rm --env-file .env reposcanner:v1; echo "exit $?"`
Expected: the last log line is `Stopped taking messages from scan-nightly: queue empty. ...`, then `exit 0`. Messages already on `scan-nightly` are scanned first; that is still exit 0.

- [ ] **Step 6: Amend ADR-017**

Append the spec's ADR-017 amendment, word for word, under `## Amendments` as `**2026-10-01 (Issue 9):** ...`. In `docs/adr/README.md`, ADR-017's status becomes `Accepted, amended 2026-09-24 and 2026-10-01 (Issues 8 and 9)`.

- [ ] **Step 7: Update `README.md`**

- **Prerequisites:** "Docker Desktop, with Linux containers, to build and run the scanner image".
- **New section `## Run in a container`**, after `## Scan from the queue locally` and before `## Known limitations`:
  - One paragraph: the image is what Azure runs; the container holds only the scanner and reaches Azurite and SQL Server on this machine through `host.docker.internal`, as the storage account and Azure SQL Database are outside the Container Apps environment; nothing starts it when a message arrives, as the scale rule does in Azure.
  - **One-off setup:** Docker Desktop; Azurite as in Scan from the queue locally, with no extra flags; the SQL login from Database and migrations, One-off setup (written in Task 5); `copy .env.example .env` and replace each `<...>`, with a table of the five settings and where each value comes from.
  - **Where settings live:** `dotnet run` and `dotnet ef` read user environment variables; the container reads only `.env`. The PAT is in both, so a renewed PAT goes in both; a `.env` with an old one exits 1 with `Azure DevOps refused the token`.
  - **Build and run:** `docker build -t reposcanner:v1 .` and `docker run --rm --env-file .env reposcanner:v1`. The exit code is the scanner's (see Exit codes). The container runs as Production, so a failed scan's message reappears after 65 minutes, not 1; add `Scanner__Execution__VisibilityTimeoutMinutes=1` to `.env` to change that.
- **Exit codes:** after the table, "In a container, the container's exit code is the scanner's."
- **Known limitations:** a "The container" group with the spec's six known limitations, one line each.
- **Repository layout:** `Dockerfile` (the scanner image), `.dockerignore` (what the image build may read), `.env.example` (the container's settings, copied to the git-ignored `.env`), in the root group.

- [ ] **Step 8: Full build and tests**

`dotnet build RepoScanner.sln` (zero warnings), then `dotnet test RepoScanner.sln`. Expected: all pass, including the docs self-scan over the amended ADR and the README.

- [ ] **Step 9: Stop for review, then commit and set #58 to Done**

Check `git status --short` does not list `.env`.

```bash
git add .env.example tests/RepoScanner.Scanner.Tests/Secrets/SelfScanTests.cs docs/adr/ADR-017-everything-runs-locally-before-azure.md docs/adr/README.md README.md
git commit -m "This commit adds .env.example, the scanner container's five settings with placeholders for the Azurite key, the PAT and the SQL password, and a self-scan test that fails if real secrets are pasted into it; documents running the image with docker run --rm --env-file .env reposcanner:v1, the container reading only the git-ignored .env while dotnet run reads user environment variables, and a renewed PAT going in both; and amends ADR-017: no docker-compose, Azurite and SQL Server on the developer's machine, reached through host.docker.internal #58"
```

---

### Task 3: The end-to-end check (#59)

**Files:**
- Create: `scripts/test-container.ps1`
- Modify: `scripts/enqueue-local.ps1`, `README.md`

**Interfaces:**
- Consumes: `reposcanner:v1` (Task 1), the complete `.env` (Task 2).
- Produces: `enqueue-local.ps1 -CorrelationId <guid>`; `test-container.ps1 [-RepositoryName victim-repo] [-Tag v1] [-SqlServer .\SQLEXPRESS] [-EnvFile <repository root>\.env]`, exiting 0 when every check passes and 1 at the first failure.

- [ ] **Step 1: Set #59 to Doing**

- [ ] **Step 2: Add `-CorrelationId` to `enqueue-local.ps1`**

`[Parameter(ParameterSetName = 'Request')] [guid] $CorrelationId = [guid]::Empty`, with a `.PARAMETER` help entry ("The message's correlation ID, which its scan run keeps. A new GUID by default; test-container.ps1 sets it to find the scan run"). An empty GUID means a new one, as `$RepositoryId` does.

Run in PowerShell: `$id = [guid]::NewGuid(); .\scripts\enqueue-local.ps1 -RepositoryName victim-repo -CorrelationId $id`
Expected: `Added to scan-nightly: {...,"correlationId":"<$id>",...}` with the same GUID. Then run `docker run --rm --env-file .env reposcanner:v1` once to scan it, which leaves the queue empty.

- [ ] **Step 3: Write `scripts/test-container.ps1`**

The repository root is `Split-Path $PSScriptRoot -Parent`; every path passed to a native command is quoted. Each check prints `PASS <name>` or `FAIL <name>: <what to do>` and the script exits 1 at the first failure. In order:

| # | Check | Passes when | On failure, says |
|---|---|---|---|
| 1 | Docker is running | `docker version` exits 0 | Start Docker Desktop and try again |
| 2 | sqlcmd is installed | `Get-Command sqlcmd` finds it | Install the SQL Server command-line tools |
| 3 | .env is complete | `-EnvFile` exists; every line that is not blank or `#` is `NAME=value`; no value starts with `"` or `'`; no value contains `<...>`; all five names are present | The file, and the line number and name of the first problem: missing file (copy `.env.example`), not `NAME=value`, quoted, still a placeholder, or which name is missing. Never the value |
| 4 | Image builds | `docker build -t reposcanner:<Tag> <root>` exits 0 | See the build output above |
| 5 | Runs as a non-root user | `id -u` inside is not `0` | The image runs as root: check `USER $APP_UID` in the Dockerfile |
| 6 | Git 2.31 or later | `git --version` inside parses to 2.31 or later | The version found |
| 7 | No .NET SDK in the image | `dotnet --list-sdks` inside prints nothing | The SDKs found: the final stage must be `runtime:10.0` |
| 8 | No appsettings.Development.json | the `sh -c "test ! -e ..."` from Task 1 exits 0 | The file is in the image: check `CopyToPublishDirectory="Never"` in the scanner's `.csproj` |
| 9 | Base images are tagged | every `FROM` in `<root>\Dockerfile` names a tag, and none is `latest` | The line |
| 10 | SQL Server reachable from a container | `docker run --rm busybox:1.37 nc -z -w 5 host.docker.internal 1433` exits 0 | Start the SQL Server (SQLEXPRESS) service, and check TCP/IP on port 1433 (README: Database and migrations) |
| 11 | Azurite reachable from a container | the same on `10001` | Start Azurite: `azurite --location c:\azurite_data` |
| 12 | A message is scanned | after `enqueue-local.ps1 -RepositoryName <name> -CorrelationId <new guid>` and `docker run --rm --env-file <EnvFile> reposcanner:<Tag>` exiting 0, the `ScanRuns` row with that `CorrelationId` has `Status` `Completed` or `PartiallyCompleted` and a 40-character `CommitSha` | The exit code, or the row's `Status` and `ErrorMessage` |
| 13 | An empty queue exits 0 with no new scan run | a second `docker run` exits 0 and `SELECT COUNT(*) FROM ScanRuns` is unchanged | The exit code, or the counts |

- SQL is read with `sqlcmd -S <SqlServer> -E -C -d RepoScanner -h -1 -W -b -Q "SET NOCOUNT ON; ..."`: Windows authentication, as `dotnet ef` uses.
- On success it prints `All 13 checks passed` and the scan run's status and commit ID.
- Comment-based help with a `.EXAMPLE`, and a `.DESCRIPTION` saying what it needs running: Docker Desktop, Azurite, SQL Server, and the PAT in `.env`.

- [ ] **Step 4: Run it as the developer would**

Run from the repository root: `powershell -NoProfile -File scripts\test-container.ps1`
Expected: 13 `PASS` lines, `All 13 checks passed`, a `victim-repo` scan run `Completed` with a 40-character commit ID, exit 0.

- [ ] **Step 5: The Review Focus checks**

| Run | Expected |
|---|---|
| From `scripts\`: `powershell -NoProfile -File "C:\Code Security Analysis EPA\RepoScanner\scripts\test-container.ps1"` | All 13 checks pass (Review Focus 1) |
| `-EnvFile` pointing at a copy of `.env` in `$env:TEMP` whose `Scanner__Queue__Name` value is `"scan-nightly"`, in quotes | `FAIL .env is complete:` naming line 4 and `Scanner__Queue__Name` as quoted, before any build, exit 1 (Review Focus 2). Delete the copy afterwards |
| `-EnvFile .env.example` | `FAIL .env is complete:` naming `Scanner__Queue__ConnectionString` as still a placeholder, exit 1 (Review Focus 3) |
| The user stops Azurite, then the script runs | checks 1 to 10 pass, then `FAIL Azurite reachable from a container: Start Azurite: azurite --location c:\azurite_data`, exit 1 (Review Focus 4). The user restarts Azurite |

Ask the user before stopping Azurite; they started it.

- [ ] **Step 6: Update `README.md`**

- In `## Run in a container`, a `### Check it end to end` subsection: what `scripts\test-container.ps1` checks, in one sentence per group (the `.env`, the image, the connections, a real scan, an empty queue), how to run it, and that messages already on `scan-nightly` are scanned too.
- **Repository layout:** under `scripts/`, `test-container.ps1  builds the scanner image and checks it, its connections and a real scan end to end`.
- **Scan from the queue locally, Add a scan request:** `-CorrelationId` in the list of the script's parameters, if that list names them.

- [ ] **Step 7: Full build and tests**

`dotnet build RepoScanner.sln` (zero warnings), then `dotnet test RepoScanner.sln`. Expected: all pass.

- [ ] **Step 8: Stop for review, then commit and set #59 to Done**

Check `git status --short` does not list `.env`.

```bash
git add scripts/test-container.ps1 scripts/enqueue-local.ps1 README.md
git commit -m "This commit adds test-container.ps1, which checks that .env is complete without printing it, builds reposcanner:v1, checks that the image runs as a non-root user with git 2.31 or later, no SDK, no Development settings and tagged base images, that a container reaches SQL Server on 1433 and Azurite on 10001, that a victim-repo message is scanned to a Completed scan run with its commit ID, and that an empty queue exits 0 with no new scan run, naming what to start or fix when a check fails; adds -CorrelationId to enqueue-local.ps1; and documents the check in the README #59"
```

---

### Task 4: Image size and caching (#60)

**Files:**
- Modify: `README.md`

**Interfaces:**
- Consumes: `reposcanner:v1` (Task 1); the `## Run in a container` section (Task 2).

- [ ] **Step 1: Set #60 to Doing**

- [ ] **Step 2: Measure the sizes**

Run: `docker pull mcr.microsoft.com/dotnet/sdk:10.0`, then `docker image ls --format "{{.Repository}}:{{.Tag}} {{.Size}}"` and note `reposcanner:v1`, `mcr.microsoft.com/dotnet/runtime:10.0` and `mcr.microsoft.com/dotnet/sdk:10.0`.
Expected: `reposcanner:v1` is a little larger than `runtime:10.0` (git and the app) and well under `sdk:10.0`.

- [ ] **Step 3: Measure a cached rebuild**

1. `docker build -t reposcanner:v1 .` once, so every step is cached.
2. Add a comment line to `src/RepoScanner.Scanner/Program.cs`, then time `docker build --progress=plain -t reposcanner:v1 .` with `Measure-Command`.
3. `git checkout -- src/RepoScanner.Scanner/Program.cs`, then build once more so `reposcanner:v1` matches the committed code.

Expected: the `RUN dotnet restore` step shows `CACHED`; only `COPY src/` and the steps after it run. Note the time, and the time of a first build with `--no-cache` for comparison.

- [ ] **Step 4: Update `README.md`**

In `## Run in a container`, a `### Image size and caching` subsection: a table of the three sizes, measured on 2026-10-01 (or the day measured); the cached rebuild time against the `--no-cache` time; and one sentence on why: the project files are copied and restored before the code, so the restore step is reused until a project file or package version changes.

- [ ] **Step 5: Stop for review, then commit and set #60 to Done**

```bash
git add README.md
git commit -m "This commit records in the README the size of reposcanner:v1 beside runtime:10.0 and sdk:10.0, and that a rebuild after a code-only change reuses the cached restore step, with the times of a cached and an uncached build #60"
```

---

### Task 5: The local SQL Server setup in the README (#70)

**Files:**
- Modify: `README.md`

**Interfaces:**
- Consumes: the login and `.env` line made on 2026-10-01 (spec, The SQL login); the `## Run in a container` section (Task 2), which links here.

- [ ] **Step 1: Ask the user what is left on #70**

They said on 2026-10-01 that #70 is not fully done. Ask what they still want to do or check (for example, looking at the login in SSMS, or changing its password) before writing the README, and do that first. Never ask for the password.

- [ ] **Step 2: Update `README.md`**

In `## Database and migrations`, `### One-off setup`, after the existing step 3, a group headed "For the scanner container (Run in a container)":

1. **TCP/IP on port 1433.** SQL Server Configuration Manager, SQL Server Network Configuration, Protocols for SQLEXPRESS: TCP/IP Enabled; in its IP Addresses tab, under IPAll, TCP Dynamic Ports empty and TCP Port `1433`. Restart the SQL Server (SQLEXPRESS) service. A port, not the instance name, so the container needs no SQL Browser.
2. **SQL Server and Windows Authentication mode.** In SSMS, the server's Properties, Security. Restart the service. `sa` stays disabled.
3. **The login.** The `CREATE LOGIN repo_scanner_local` script from #70, with `<long random password>` as its placeholder: db_datareader and db_datawriter on `RepoScanner` only, the same rights as the scanner in Azure (ADR-008), so `dotnet ef` keeps using Windows authentication.
4. **The password goes only in `.env`**, in `Scanner__Database__ConnectionString`, never in the repository.
5. **Check it:** `sqlcmd -S localhost,1433 -U repo_scanner_local -d RepoScanner -C -Q "SELECT SUSER_NAME(), DB_NAME()"` (it asks for the password) gives the login and database; a `CREATE TABLE` as the login is refused.

Also no Windows Firewall rule is needed: Docker Desktop passes `host.docker.internal` to this machine's 127.0.0.1.

- [ ] **Step 3: Full build and tests**

`dotnet build RepoScanner.sln` (zero warnings), then `dotnet test RepoScanner.sln`. Expected: all pass, including the docs self-scan over the README's login script.

- [ ] **Step 4: Stop for review, then commit and set #70 to Done**

Check `git status --short` does not list `.env`.

```bash
git add README.md
git commit -m "This commit documents the local SQL Server setup the scanner container needs: TCP/IP on port 1433, mixed-mode authentication with sa left disabled, the repo_scanner_local login with db_datareader and db_datawriter only, its password kept only in the git-ignored .env, and how to check it #70"
```

Then tell the user Issue 9's tasks are done, run `scripts\test-container.ps1` once more on the final branch, and ask whether to push and open the pull request.
