# Issue 9: Containerisation and local docker run (design)

- **Status:** Draft, for review
- **Date:** 2026-10-01
- **Work item:** Issue 9, #56 (tasks #57 to #60 and #70)
- **Branch:** `feature/56-containerisation-and-local-docker-run`

## Purpose

The scanner runs today with `dotnet run` on the developer's machine. In Azure it runs as a Container Apps Job, from an image (ADR-001, ADR-009). This Issue builds that image and proves it works locally, the way Azure will run it: the scanner alone in a container, reading its queue in Azurite and saving to SQL Server, both outside the container, as the storage account and Azure SQL Database are outside the Container Apps environment in Azure.

There is no docker-compose. With Azurite and SQL Server on the developer's machine, Compose would hold one service, the scanner, and be no more than a saved `docker run` command.

What happens for one local run:

1. `docker build -t reposcanner:v1 .` compiles the scanner with the .NET SDK, then copies only the published app into a .NET runtime image with git.
2. `.\scripts\enqueue-local.ps1 -RepositoryName victim-repo` puts a scan request on `scan-nightly` in Azurite.
3. `docker run --rm --env-file .env reposcanner:v1` starts the scanner as the user `app`, with every setting from `.env`.
4. The scanner reads the message from Azurite through `host.docker.internal:10001`, clones `victim-repo`, scans it, and saves the findings to SQL Server through `host.docker.internal,1433`, signing in as `repo_scanner_local`.
5. The queue is empty, so the scanner exits 0, and so does the container. `--rm` deletes the stopped container.

Nothing starts the container when a message arrives locally: in Azure that is the job's `azure-queue` scale rule. Locally it is started by hand.

## Success criteria

From Issue 9 (#56):

1. `docker run --rm --env-file .env reposcanner:v1` exits 0 on an empty queue and processes a message when one exists.
2. The image is built from the runtime base, not the SDK.
3. No `:latest` tag is referenced.
4. The container connects to the local SQL Server with the SQL login, and no SQL password is committed to the repository.
5. The one-off local SQL Server setup (TCP/IP on port 1433, mixed-mode authentication, the SQL login) is documented in the README.
6. There is no docker-compose file in the repository.

## Decisions

Agreed in the design discussion on 2026-10-01.

| # | Question | Decision | Reason |
|---|---|---|---|
| 1 | docker-compose | None. `docker build` and `docker run` | Azurite and SQL Server stay on the developer's machine, as their Azure counterparts are outside the Container Apps environment, so Compose would hold only the scanner. Amends ADR-017 |
| 2 | Where the container's settings live | All in the git-ignored `.env` file at the repository root, passed with `docker run --env-file .env`. Runs on Windows (`dotnet run`, `dotnet ef`) keep reading user environment variables | One place per way of running, so nothing is passed on the command line. The two cannot share one place: the database connection string differs (decision 13) |
| 3 | The PAT | In `.env` as well as the user environment variable `Scanner__AzureDevOps__Pat`. When it is renewed, both are updated | Keeps decision 2's rule with no exception. A `.env` left with an old PAT fails loudly: exit 1 with `Azure DevOps refused the token` (Issue 8 design, decision 13) |
| 4 | Environment | Production, the default when `DOTNET_ENVIRONMENT` is not set, as in Azure. `appsettings.Development.json` is left out of the published app | Proves the image needs nothing but environment variables, which is how Azure configures the job. Leaving the file out means no Development setting can reach the image by accident |
| 5 | Base images | `mcr.microsoft.com/dotnet/sdk:10.0` to build, `mcr.microsoft.com/dotnet/runtime:10.0` to run. Tagged `10.0`, never `latest`, an exact patch or a digest | A rebuild picks up Microsoft's newest .NET 10 security patch and never moves to .NET 11. On 2026-10-01 `sdk:10.0` held SDK 10.0.401, the version `global.json` names, and `runtime:10.0` held runtime 10.0.12 on Ubuntu 24.04.5 |
| 6 | Runtime, not SDK | Two stages: the SDK stage compiles and publishes; the final stage holds the runtime, git and the published app | The scanner never runs a `dotnet` command (checked on 2026-10-01): Roslyn and NuGet.Versioning are libraries in the app. The SDK image is several times bigger, and every tool in it is something more to patch |
| 7 | Git | `apt-get install --no-install-recommends git` in the final stage: git 2.43.0 on Ubuntu 24.04 | The clone runs git (ADR-016), which needs 2.31 or later for `GIT_CONFIG_COUNT` (Issue 8 design, decision 1). `ca-certificates`, which git needs for https, is already in the runtime image (checked on 2026-10-01). Microsoft's chiseled images have no package manager, so git cannot be added to them |
| 8 | User | `USER $APP_UID`: the user `app`, number 1654, which Microsoft's .NET images include | Containers run as root unless told otherwise. The scanner clones and reads other people's code, so if a repository ever exploited it, a normal user can do far less. The number, not the name, lets a platform check that the user is not root. `/tmp/reposcanner`, where the clones go, is writable by `app` |
| 9 | How the app starts | `ENTRYPOINT ["dotnet", "RepoScanner.Scanner.dll"]`, in exec form | The app is the container's first process, so its exit code is the container's exit code, and `docker stop` or Azure's SIGTERM reaches it directly, which the host already handles (Issue 7) |
| 10 | Build speed | Copy the project files and the shared build files, restore, then copy the code and publish | Docker reuses the restore step until a project file or package version changes, so a code-only change does not download the packages again (#60) |
| 11 | What reaches the build | `.dockerignore` leaves out everything except `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig` and `src/`, and leaves out `bin/` and `obj/` under `src/` | Listing what goes in, rather than what stays out, means `.env` and any secret file added later can never reach an image. `.editorconfig` goes in because the build enforces its style rules as errors (`EnforceCodeStyleInBuild`, `TreatWarningsAsErrors`) |
| 12 | The local image tag | `reposcanner:v1` | A version that names what was built, never `latest`. Images deployed to Azure are tagged with the build ID instead (ADR-005), in Issue 12 |
| 13 | Reaching the developer's machine | `host.docker.internal`: port 10001 for Azurite, 1433 for SQL Server | Docker Desktop's name for the machine the container runs on. It passes the traffic to the machine's own 127.0.0.1, so Azurite, which listens on 127.0.0.1 only, needs no `--queueHost 0.0.0.0`, and SQL Server needs no Windows Firewall rule (both checked on 2026-10-01 with `nc` and `wget` from a container) |
| 14 | Signing in to SQL Server | The SQL login `repo_scanner_local`, with db_datareader and db_datawriter on `RepoScanner` only. Created on 2026-10-01 (#70) | A Linux container cannot use Windows authentication. The same rights as `id-scanner` in Azure (ADR-008), so a schema change attempted by the scanner fails locally before it can fail in Azure. `dotnet ef` keeps using Windows authentication, since it creates and changes tables |
| 15 | The end-to-end check | `scripts/test-container.ps1` | One repeatable command that a pipeline step in Issue 12 can reuse. An xUnit test driving Docker would make every `dotnet test` run need Docker Desktop and take minutes |
| 16 | Matching a message to its scan run | `enqueue-local.ps1` gets an optional `-CorrelationId` | The script sets the correlation ID, so it knows which `ScanRuns` row to check |
| 17 | `--path` in the container | Not supported | It would need a folder mounted into the container. The image is for queue mode, as in Azure; `dotnet run -- --path` still scans a local folder |
| 18 | New packages | None | |
| 19 | New ADRs | None. ADR-017 is amended (#58) | See ADR amendment |
| 20 | Where this document lives | `docs/design/` | Same as Issues 2 to 8 |

## Project layout

```
Dockerfile                               the two-stage image                    #57
.dockerignore                            only global.json, the shared build
                                         files, .editorconfig and src/ go in    #57
.env.example                             the container's settings, with
                                         placeholders                           #58
.env                                     git-ignored, never committed           #58, #70

src/RepoScanner.Scanner/
  RepoScanner.Scanner.csproj             appsettings.Development.json left out
                                         of the published app                   #57

scripts/enqueue-local.ps1                -CorrelationId                         #59
scripts/test-container.ps1               the end-to-end check                   #59

docs/adr/ADR-017-everything-runs-locally-before-azure.md   amended              #58
docs/adr/README.md                       ADR-017 marked amended again           #58
README.md                                                                       #58, #59, #60, #70
```

No C# changes. `.gitignore` already ignores `.env`, and not `.env.example`.

## The image (#57)

```dockerfile
# Stage 1: compile with the .NET SDK. Nothing from this stage ships except the published app.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

# The project files and shared build files first, so the restore step is reused until one of them changes
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/RepoScanner.Core/RepoScanner.Core.csproj src/RepoScanner.Core/
COPY src/RepoScanner.Data/RepoScanner.Data.csproj src/RepoScanner.Data/
COPY src/RepoScanner.Scanner/RepoScanner.Scanner.csproj src/RepoScanner.Scanner/
RUN dotnet restore src/RepoScanner.Scanner/RepoScanner.Scanner.csproj

COPY src/ src/
RUN dotnet publish src/RepoScanner.Scanner/RepoScanner.Scanner.csproj --configuration Release --no-restore --output /app -p:UseAppHost=false

# Stage 2: what ships. The .NET runtime, git for the clone (ADR-016) and the published app.
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
RUN apt-get update \
    && apt-get install --yes --no-install-recommends git \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app .

# The image's own non-root user, app (1654)
USER $APP_UID
ENTRYPOINT ["dotnet", "RepoScanner.Scanner.dll"]
```

- `-p:UseAppHost=false` skips the native `RepoScanner.Scanner` launcher, which `dotnet RepoScanner.Scanner.dll` does not need.
- Removing apt's package lists in the same step keeps them out of the image.
- `global.json` is copied, so the build fails if `sdk:10.0` ever holds an SDK older than the one the developer uses, rather than quietly building with it.

`.dockerignore`:

```
# Everything stays out of the build except what the Dockerfile copies
*
!global.json
!Directory.Build.props
!Directory.Packages.props
!.editorconfig
!src/
src/**/bin/
src/**/obj/
```

`RepoScanner.Scanner.csproj` keeps `appsettings.Development.json` out of the published app, while `dotnet run` still copies it next to the built app:

```xml
<None Update="appsettings*.json" CopyToOutputDirectory="PreserveNewest" />
<None Update="appsettings.Development.json" CopyToPublishDirectory="Never" />
```

## Running it (#58)

```bash
docker run --rm --env-file .env reposcanner:v1
```

The exit code is the scanner's, as in the README: 0 when the queue is done or empty, 1 when the scanner failed (including a rejected PAT), 2 when a setting is missing or wrong.

### .env

Git-ignored, at the repository root. Each line is `NAME=value`, with no quotes: Docker takes everything after the first `=` as the value.

| Setting | Value |
|---|---|
| `Scanner__Queue__Name` | `scan-nightly` |
| `Scanner__Queue__ConnectionString` | `DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=<Azurite's account key>;QueueEndpoint=http://host.docker.internal:10001/devstoreaccount1;` |
| `Scanner__AzureDevOps__OrganizationUrl` | `https://dev.azure.com/BlacklightInternalsMo` |
| `Scanner__AzureDevOps__Pat` | The same PAT as the user environment variable |
| `Scanner__Database__ConnectionString` | `Server=host.docker.internal,1433;Database=RepoScanner;User ID=repo_scanner_local;Password=<password>;Encrypt=True;TrustServerCertificate=True` |

- `UseDevelopmentStorage=true` does not work in the container: it points at 127.0.0.1, which there is the container itself. The full form names Azurite's well-known account `devstoreaccount1`, whose key Microsoft publishes in the Azurite documentation.
- Every other setting keeps its Azure default: for example, a scan that crashes leaves its message hidden for 65 minutes (`Scanner:Execution:VisibilityTimeoutMinutes`), not Development's 1. A line in `.env` changes one.
- #58 copies the PAT from the user environment variable into `.env` without printing it. `.env` already holds the database line, written when the login was created (#70).

### .env.example

Committed, with the same five lines and `<...>` placeholders for Azurite's account key, the PAT and the password. A new developer copies it to `.env` and fills them in. RepoScanner's own secret scanner treats `<NAME>` as a placeholder (Issue 3 design, Placeholder allow-list), so a self-scan reports nothing in it. The real Azurite key is not written in it: it is public, but SEC002 would still report it.

### ADR amendment

**ADR-017, added in #58.** *2026-10-01 (Issue 9): there is no docker-compose. With Azurite and SQL Server both on the developer's machine, as their Azure counterparts are outside the Container Apps environment, Compose would hold only the scanner. The scanner image is built with `docker build` and run with `docker run --env-file .env`. The container reaches Azurite and SQL Server through `host.docker.internal`, which Docker Desktop passes to the machine's own 127.0.0.1. Runs on Windows read user environment variables; the container reads only the git-ignored `.env` file, which holds its own copy of the PAT and the SQL login's connection string. This replaces the docker-compose parts of the 2026-09-24 and 2026-10-01 amendments. The decision is otherwise unchanged.*

## The SQL login (#70)

Done on 2026-10-01:

- Mixed-mode authentication was already on, and `sa` is disabled. TCP/IP listens on port 1433.
- `repo_scanner_local` was created with `CHECK_POLICY = ON`, default database `RepoScanner`, and a random 40-character password written straight to `.env` and never printed.
- Its `RepoScanner` user is a member of db_datareader and db_datawriter only.
- Signed in on `localhost,1433`, it reads `ScanRuns`; `CREATE TABLE` is refused with `CREATE TABLE permission denied`.
- `nc -zv host.docker.internal 1433` from a `busybox:1.37` container connects, with no Windows Firewall rule.

What remains is the README's one-off setup steps (see README).

## The end-to-end check (#59)

`scripts/test-container.ps1`, run from the repository root with Docker Desktop, Azurite and SQL Server running. It stops at the first failed check and exits 1, or prints each check as it passes and exits 0.

1. **Build** `reposcanner:v1`.
2. **The image:**
   - `id -u` inside is not 0.
   - `git --version` inside is 2.31 or later.
   - `dotnet --list-sdks` inside lists nothing.
   - `/app/appsettings.Development.json` does not exist inside.
   - Every `FROM` in the Dockerfile has a tag, and none is `latest`.
3. **The connections:** `nc -zv host.docker.internal 1433` and `10001` from `busybox:1.37` connect.
4. **A message is processed:** a new correlation ID is passed to `enqueue-local.ps1 -RepositoryName victim-repo -CorrelationId <id>`, the container runs and exits 0, and the `ScanRuns` row with that correlation ID is `Completed` or `PartiallyCompleted` with a 40-character `CommitSha`. The script reads SQL Server with Windows authentication through `sqlcmd`, as `dotnet ef` does.
5. **An empty queue:** the container runs again and exits 0, and the number of `ScanRuns` rows has not changed.

Step 5 proves the empty-queue case without deleting anything from the developer's queue: step 4 leaves it empty, since the scanner takes every message before exiting. Messages already on the queue are scanned in step 4 too.

`enqueue-local.ps1` gets `-CorrelationId <guid>`, optional, a new GUID by default as today.

Parameters: `-RepositoryName` (`victim-repo`), `-Tag` (`v1`), `-SqlServer` (`.\SQLEXPRESS`).

## Image size and caching (#60)

Measured once the image builds and recorded in the README:

- The size of `reposcanner:v1` (`docker image ls`), beside `runtime:10.0` and `sdk:10.0`.
- A rebuild after changing one `.cs` file shows the restore step as `CACHED`, and how long it took compared with a first build.

## README

- **Prerequisites:** Docker Desktop, for the container.
- **Database and migrations, One-off setup:** TCP/IP on port 1433 in SQL Server Configuration Manager, mixed-mode authentication in SSMS (Server properties, Security, then a service restart), and the `repo_scanner_local` script, with the password going only to `.env` (#70).
- **A new section, Run in a container:** copying `.env.example` to `.env` and filling it in; that `.env` is for the container and user environment variables for `dotnet run`; that a renewed PAT goes in both; `docker build -t reposcanner:v1 .`; `docker run --rm --env-file .env reposcanner:v1`; `scripts/test-container.ps1`; image size and caching (#58 to #60).
- **Exit codes:** the container's exit code is the scanner's.
- **Repository layout:** `Dockerfile`, `.dockerignore`, `.env.example` and `scripts/test-container.ps1`.
- **Known limitations:** the limits below.

## Testing

No C# changes, so `dotnet test` is unchanged and needs no Docker. The checks are in `scripts/test-container.ps1`.

| Task | Checks |
|---|---|
| #57 | `docker build` succeeds with no warnings; the image runs as `app`; git 2.31 or later; no SDK; no `appsettings.Development.json`; no `latest`. Done by hand while writing the Dockerfile, then by the script from #59 |
| #58 | `docker run` with `.env` and an empty queue exits 0. Without `.env` it exits 2, listing the missing settings |
| #70 | The checks above, already passed |
| #59 | The script passes end to end against `victim-repo`: Completed, findings saved, `CommitSha` matching `victim-repo`'s latest commit on `main`, and exit 0 on the empty queue after |
| #60 | The cached rebuild after a one-line `.cs` change |

## Order of work

One task at a time, stopping after each for review, then a commit whose message starts "This commit" and ends in its task number (ADR-020):

1. **#56** This design document.
2. **#57** `Dockerfile`, `.dockerignore`, and `appsettings.Development.json` left out of the published app.
3. **#58** `.env.example`, the PAT copied into `.env`, the ADR-017 amendment, and the README's Run in a container section.
4. **#70** The README's SQL Server one-off setup.
5. **#59** `test-container.ps1`, `enqueue-local.ps1 -CorrelationId`, and the end-to-end run.
6. **#60** Image size and caching in the README.

## Not in Issue 9

| Item | Where it is on the board |
|---|---|
| Pushing the image to Azure Container Registry, tagging it with the build ID (ADR-005), and running the script in a pipeline | Issue 12 |
| Scanning the image for vulnerable packages | Not on the board yet; a candidate task for Issue 12, whose tasks are added when the pipeline is planned |
| `--path` in the container | Not on the board (decision 17) |
| The enqueuer's image | Issue 5 of the Enqueuer console app epic, which reaches Azurite the same way |

## Known limitations

- `10.0` moves: two builds a month apart can have different .NET 10 patches and Ubuntu packages, including git.
- `host.docker.internal` comes with Docker Desktop. A Linux machine, such as a pipeline agent in Issue 12, needs `--add-host=host.docker.internal:host-gateway`.
- The PAT is in two places locally, and both need renewing.
- `.env` holds the PAT and the SQL password in plain text. It is git-ignored and kept out of the image, but anyone who can read the repository folder can read it.
- The image is built for the developer's machine, linux/amd64, which is what Container Apps runs.
- Locally nothing starts the container when a message arrives; it is run by hand.
