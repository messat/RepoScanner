# Issue 8: Repository acquisition (design)

- **Status:** Draft, for review
- **Date:** 2026-10-01
- **Work item:** Issue 8, #36 (tasks #37 to #41)
- **Branch:** `feature/36-repository-acquisition`

## Purpose

In queue mode the scanner finds a repository's code in a folder on the developer's machine (`Scanner:Source:LocalRoot`, Issue 7). This Issue makes it fetch the code itself: it clones the latest commit of the repository's default branch from Azure DevOps into a new temporary folder, scans it, saves the commit ID on the scan run, and deletes the folder, even when the scan fails.

The clone sits behind `IRepositorySource`, the slot Issue 7 left for it, so the queue loop does not change. `LocalFolderSource` and `Scanner:Source:LocalRoot` are removed. `--path` stays: it scans a folder on the developer's machine with no queue, no Azure DevOps and no token.

What happens for one queue message:

1. The scan run starts, as it does today.
2. A new folder is made, such as `%TEMP%\reposcanner\3f9c0a1b7d2e`.
3. `git clone --depth 1 --single-branch --branch main -- <url> <folder>` runs, with the PAT in environment variables on the git process only.
4. `git rev-parse HEAD` reads the commit ID.
5. The three scanners run on the folder, and the commit ID is saved on the scan run.
6. The folder is deleted, whether the scan passed or failed.

## Success criteria

From Issue 8 (#36):

1. A queue message for a real repository produces findings from real source.
2. Process arguments and logs never contain the PAT.
3. The workspace folder is gone after the run, including on failure.

## Decisions

Agreed in the design discussion on 2026-10-01.

| # | Question | Decision | Reason |
|---|---|---|---|
| 1 | How git gets the PAT | In environment variables set on the git process only: `GIT_CONFIG_COUNT=1`, `GIT_CONFIG_KEY_0=http.extraheader` and `GIT_CONFIG_VALUE_0=AUTHORIZATION: Basic <Base64 of ":" + PAT>`. Never in git's arguments, the URL or a file | Arguments show in process lists (Task Manager's Command line column, `ps`) and crash dumps, which criterion 2 rules out. Git reads `GIT_CONFIG_*` as it reads `-c` (Git 2.31 and later), and does not save it in the clone's `.git/config` (checked with Git 2.50 on 2026-10-01). Amends ADR-016, which said `-c http.extraheader` |
| 2 | Where the PAT is kept locally | The user environment variable `Scanner__AzureDevOps__Pat` | No set-up beyond `setx`, never in the repository, and it is how Azure hands the PAT to the job too, so local runs match Azure. dotnet user-secrets is not used: it needs `dotnet user-secrets init`, and does not reach a Docker container (Issue 9) without mounting its folder. Amends ADR-017 |
| 3 | Where the PAT is kept in Azure | Key Vault, as ADR-007 decided. The job's secret reference gives it to the app as the same environment variable | Unchanged. Set up in the deployment Issues |
| 4 | One PAT or two | One PAT with Code (Read) only, shared by the enqueuer and both scanner jobs | Both need the same permission: listing repositories (`GET https://dev.azure.com/{organization}/_apis/git/repositories`) needs `vso.code`, as cloning does. One secret, as ADR-007 says, and one expiry to watch. ADR-014 already runs all three as `id-scanner`. Two PATs with the same scope would add a secret and gain nothing |
| 5 | Which organisation | One per scanner deployment, from `Scanner:AzureDevOps:OrganizationUrl`. The message gives the project and repository | A PAT belongs to one organisation, and the message format (Issue 7) has no organisation field. Locally it is `https://dev.azure.com/BlacklightInternalsMo`, where `victim-repo` is |
| 6 | How git is run | The git command line, through `Process` with `ArgumentList`, as ADR-016 decided | LibGit2Sharp has no reliable shallow clone (ADR-016) |
| 7 | The clone command | `git -c credential.helper= -c core.longpaths=true clone --depth 1 --single-branch --branch <defaultBranch> -- <url> <folder>` | The latest commit of one branch only (ADR-016). `credential.helper=` switches off any sign-in helper, such as Git Credential Manager on Windows, which could open a sign-in window, or quietly use the developer's own saved login, when the PAT is rejected; it holds no secret, so it may be an argument. `core.longpaths` lets deep paths clone on Windows and is ignored elsewhere. `--` stops a URL or folder being read as an option |
| 8 | Prompts | `GIT_TERMINAL_PROMPT=0` and `GCM_INTERACTIVE=never` on every git process | A rejected PAT fails in seconds instead of waiting for input that never comes (ADR-016) |
| 9 | The commit ID | `git rev-parse HEAD` after the clone. It must be 40 hexadecimal characters | `ScanRun.CommitSha` is `varchar(40)`, and Azure DevOps uses SHA-1 |
| 10 | Where the clone goes | A new folder for each clone: `<temp>/reposcanner/<12 random hexadecimal characters>` | The container's temporary directory (ADR-016), and a fresh name each time, so two scans never share a folder. The root is passed in, so tests use their own |
| 11 | Deleting the folder | When the workspace is closed, after clearing read-only attributes on the files inside, without following links. A failed delete logs a warning | Git marks its pack files read-only on Windows, and `Directory.Delete` then fails (checked on 2026-10-01). A failed delete must not hide the scan's own result |
| 12 | Time limit | `Scanner:AzureDevOps:CloneTimeoutMinutes`, 8 by default. On reaching it, git and its child processes are stopped | ADR-016's eight-minute cap. `ScanTimeoutPerRepoMinutes` (15) still covers the clone and the scan together |
| 13 | A rejected PAT | Recognised from git's error text. The scan run is marked Failed with `Azure DevOps refused the token ...`, the run stops, and the process exits 1. The message is left | Every other repository would fail the same way, as with the database out of reach (Issue 7 design, The loop), and stopping saves them from being poisoned. "Scan now" and local runs reach the scanner without the enqueuer, so the scanner is often the first to find out, and its Failed scan run shows the reason in the dashboard |
| 14 | Any other clone failure | The scan run is marked Failed with git's reason. The message is retried, then poisoned, as Issue 7 does | A missing, renamed or hidden repository, or a missing branch, affects only its own message |
| 15 | `LocalFolderSource` and `Scanner:Source:LocalRoot` | Removed, with their tests | Issue 7 built them for its demo only. `--path` still scans a local folder |
| 16 | Logging | One line per clone with the repository, branch, commit ID, duration, and free disk space before and after. Never the PAT | ADR-016 asks for `DriveInfo` before and after: the replica's disk is about 2 GiB |
| 17 | New packages | None | `Process`, `DriveInfo` and `Convert.ToBase64String` are part of .NET |
| 18 | New ADRs | None. ADR-016 and ADR-017 are amended (#37) | See ADR amendments |
| 19 | Where this document lives | `docs/design/` | Same as Issues 2 to 7 |

## Project layout

```
src/RepoScanner.Scanner/
  Configuration/   AzureDevOpsOptions                                         #37, #40
                   SourceOptions                                      removed  #37
  Git/             GitCommand, GitProcess, GitCommandException                #37, #40
  Sources/         WorkspaceFolders                                           #39
                   CloneUrl, AzureDevOpsCloneSource,
                   AzureDevOpsAuthenticationException                         #37
                   RepositoryWorkspace: + CommitSha                           #38
                   LocalFolderSource                                  removed  #37
  RepositoryScanRunner.cs   saves the workspace's commit ID                   #38
  ScanOrchestrator.cs       a rejected PAT stops the run                      #37
  Program.cs                registrations; a rejected PAT exits 1             #37
  appsettings.Development.json   Scanner:AzureDevOps:OrganizationUrl          #37

scripts/enqueue-local.ps1                defaults: project Kudos Wall, branch main  #41

tests/RepoScanner.Scanner.Tests/
  Git/             GitCommand and GitProcess tests                            #37, #40
  Sources/         WorkspaceFolders, CloneUrl and AzureDevOpsCloneSource tests,
                   BareRepository (test helper)                               #37 to #40
                   LocalFolderSourceTests                             removed  #37
  Configuration/   AzureDevOpsOptionsTests                                    #37, #40
                   SourceOptionsTests                                 removed  #37
  ProgramTests.cs, DatabaseRegistrationTests.cs, ScanOrchestratorTests.cs   updated  #37
  RepositoryScanRunnerTests.cs           the commit ID                        #38
  QueueModeTests.cs                      clones local bare repositories      #41

docs/adr/ADR-016-shallow-single-branch-clone-with-the-credential-in-a-header.md   amended  #37
docs/adr/ADR-017-everything-runs-locally-before-azure.md                          amended  #37
docs/adr/README.md                       both marked amended 2026-10-01       #37
README.md                                                                     #41
```

Classes are `sealed` or `static` and follow ADR-021, as in Issues 2 to 7. `AzureDevOpsCloneSource`, `GitProcess` and `WorkspaceFolders` are singletons: they keep nothing between clones.

## The folder (#39)

```csharp
namespace RepoScanner.Scanner.Sources
{
    // One new folder per clone, under one root: <temp>/reposcanner in the app, a test's own folder in tests
    public sealed class WorkspaceFolders
    {
        public WorkspaceFolders(string root, ILogger<WorkspaceFolders> logger);

        // A new, empty folder with a random 12-character name, creating the root if it is missing
        public string Create();

        // Deletes the folder and everything in it, clearing read-only attributes first. Logs a warning and
        // returns, rather than throwing, when it cannot
        public void Delete(string folder);
    }
}
```

- Read-only attributes are cleared by walking the folder without following links (reparse points), so a link in a cloned repository can never change a file outside it.
- `Delete` refuses, with a warning, a folder that is not directly inside the root, as a guard against ever deleting the wrong folder.
- Program registers it with the root `Path.Combine(Path.GetTempPath(), "reposcanner")`.

## The clone (#37)

### Settings

```csharp
namespace RepoScanner.Scanner.Configuration
{
    // Where queue mode clones from, bound from the "Scanner:AzureDevOps" section and validated when the host starts
    // (ADR-022). Queue mode only: --path needs no Azure DevOps.
    public sealed record AzureDevOpsOptions : IValidatableObject
    {
        public const string SectionName = "Scanner:AzureDevOps";

        // https://dev.azure.com/<organisation>; a file:/// folder of bare repositories in tests
        public string OrganizationUrl { get; init; } = string.Empty;

        // A personal access token with Code (Read) only, shared with the enqueuer. ToString shows it as ***.
        public string Pat { get; init; } = string.Empty;
    }
}
```

| Setting | Rule | When it is wrong |
|---|---|---|
| `OrganizationUrl` | Required. An absolute `https` URL, or `file` for tests, with no user name or password in it. A trailing `/` is ignored | `Scanner:AzureDevOps:OrganizationUrl must be an https URL such as https://dev.azure.com/<organisation>, with no user name in it` |
| `Pat` | Required | `Scanner:AzureDevOps:Pat is required in queue mode. Locally, set the Scanner__AzureDevOps__Pat user environment variable to a personal access token with Code (Read).` A failure message never includes the value |

A C# record's `ToString` prints every property, so `AzureDevOpsOptions` overrides it to print `Pat = ***`.

### The URL

```csharp
// <OrganizationUrl>/<project>/_git/<repository>, each name escaped, so "Kudos Wall" becomes Kudos%20Wall
public static class CloneUrl
{
    public static string For(string organizationUrl, string projectName, string repositoryName);
}
```

`https://dev.azure.com/BlacklightInternalsMo/Kudos%20Wall/_git/victim-repo`. Git decodes `%20` in `file:///` URLs too (checked on 2026-10-01), so tests use the same code with a project name that has a space.

### Running git

```csharp
namespace RepoScanner.Scanner.Git
{
    // One git command: its arguments, the environment variables set on its process only, and the values to wipe
    // from anything it prints. Built here and run by GitProcess, so tests can check where the PAT goes without
    // running git. ToString shows the arguments only.
    public sealed record GitCommand(IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment, IReadOnlyList<string> Secrets)
    {
        public static GitCommand Clone(string url, string branch, string folder, string pat);

        public static GitCommand RevParseHead(string folder);
    }

    public sealed class GitProcess
    {
        public GitProcess(ILogger<GitProcess> logger);

        // Runs git from PATH and returns what it printed to standard output. A non-zero exit throws
        // GitCommandException with its exit code and the last lines it printed to standard error, every secret
        // replaced by ***. Cancelling stops git and its child processes and throws OperationCanceledException.
        public Task<string> RunAsync(GitCommand command, CancellationToken cancellationToken);
    }
}
```

- Every command sets `GIT_TERMINAL_PROMPT=0` and `GCM_INTERACTIVE=never`. `Clone` also sets `GIT_CONFIG_COUNT`, `GIT_CONFIG_KEY_0` and `GIT_CONFIG_VALUE_0` (decision 1), and its `Secrets` are the PAT and its Base64 form.
- Standard output and standard error are read while git runs, so a full pipe never stalls it. Only the last 20 lines of standard error are kept.
- Stopping uses `Process.Kill(entireProcessTree: true)`, because git starts helper processes (`git-remote-https`) that would otherwise carry on downloading.

### The source

```csharp
namespace RepoScanner.Scanner.Sources
{
    // Queue mode's code: a shallow clone of the message's repository and branch (Issue 8)
    public sealed class AzureDevOpsCloneSource : IRepositorySource
    {
        public AzureDevOpsCloneSource(IOptions<AzureDevOpsOptions> options, GitProcess git, WorkspaceFolders folders, ILogger<AzureDevOpsCloneSource> logger);
    }
}
```

`OpenAsync(request, cancellationToken)`:

1. Builds the URL from `OrganizationUrl`, `request.ProjectName` and `request.RepositoryName`.
2. Makes a new folder, and notes the free space on its drive.
3. Clones `request.DefaultBranch` into it (`GitCommand.Clone`).
4. Reads the commit ID (`GitCommand.RevParseHead`, #38).
5. Logs `Cloned {ProjectName}/{RepositoryName} {Branch} at {CommitSha} in {DurationMs} ms; {FreeMegabytes} MB free on {Drive}, {FreeMegabytesBefore} before`.
6. Returns a `RepositoryWorkspace` for the folder whose closing deletes it.

If any step after 2 fails, the folder is deleted before the error goes on, so it is gone even though no workspace was returned.

### A rejected PAT

A `GitCommandException` from the clone whose error text contains `terminal prompts disabled`, `Authentication failed`, `returned error: 401` or `returned error: 403`, ignoring case, becomes:

```csharp
// Azure DevOps refused the PAT: every repository would fail the same way, so the run stops (decision 13)
public sealed class AzureDevOpsAuthenticationException : Exception
```

with the message `Azure DevOps refused the token in Scanner:AzureDevOps:Pat for https://dev.azure.com/BlacklightInternalsMo. Check that it has not expired or been revoked, and that it has Code (Read).` Checked on 2026-10-01 against `victim-repo` with a fake token: git exited 128 with `fatal: could not read Username for '<url>': terminal prompts disabled` within seconds, and no sign-in window opened.

What happens:

1. Git fails, and the source throws `AzureDevOpsAuthenticationException`.
2. `RepositoryScanRunner` marks the scan run Failed with that message, as it does any failure, and rethrows.
3. `ScanOrchestrator.HandleAsync` lets this exception through instead of logging it and moving on. `RunAsync` counts it as failed, logs `Stopped taking messages from {QueueName}: Azure DevOps refused the token. ...` with the counts, and rethrows.
4. `Program` logs `Azure DevOps refused the token, scanner stopped: {Problem}` as one line, with no stack trace, and exits 1.
5. The message is left. It reappears after the visibility timeout, and the messages behind it are not touched. If the PAT stays broken, that one message is poisoned on its 4th pick-up.

Any other `GitCommandException` is an ordinary failed scan (decision 14), with git's reason as the scan run's `ErrorMessage`, such as `git clone failed with exit code 128: remote: TF401019: The Git repository with name or identifier ghost does not exist or you do not have permissions for the operation you are attempting.`

### Registration and removals

- Program registers `AzureDevOpsOptions` (bound, validated, checked at start-up), `GitProcess`, `WorkspaceFolders` and `AzureDevOpsCloneSource` as `IRepositorySource`, in queue mode only, in place of `SourceOptions` and `LocalFolderSource`, which are deleted with their tests.
- `appsettings.Development.json` gets `"AzureDevOps": { "OrganizationUrl": "https://dev.azure.com/BlacklightInternalsMo" }`. The organisation is not a secret. The PAT never goes in an appsettings file.
- The developer's `Scanner__Source__LocalRoot` user environment variable is then unused and harmless. #37 ends by deleting it from the developer's machine:

  ```powershell
  [Environment]::SetEnvironmentVariable("Scanner__Source__LocalRoot", $null, "User")
  ```

### ADR amendments

**ADR-016, added in #37.** *2026-10-01 (Issue 8): the PAT reaches git in environment variables on the git process (`GIT_CONFIG_COUNT`, `GIT_CONFIG_KEY_0=http.extraheader`, `GIT_CONFIG_VALUE_0`), not as a `-c` argument, so it never appears in a process list. `-c credential.helper=` switches off any sign-in helper, and `GCM_INTERACTIVE=never` joins `GIT_TERMINAL_PROMPT=0`, so a rejected PAT fails rather than prompting or falling back to a saved login. A rejected PAT stops the run with exit 1 and leaves the message, since every repository would fail the same way. The PAT, Code (Read) only, is shared with the enqueuer, which needs the same scope to list repositories. The decision is otherwise unchanged.*

**ADR-017, added in #37.** *2026-10-01 (Issue 8): locally the PAT is the user environment variable `Scanner__AzureDevOps__Pat`, not dotnet user-secrets: it needs no set-up in the project, and docker-compose can pass it to the scanner container in Issue 9. In Azure it is still a Key Vault secret reference, which reaches the job as the same environment variable. The decision is otherwise unchanged.*

ADR-007 needs no change: the PAT still reaches git as an `http.extraheader`, never in a URL.

## The commit ID (#38)

- `RepositoryWorkspace` gets `public string? CommitSha { get; }`, set through its constructor. `ForExistingFolder` gives null, so `--path` saves none.
- The clone source reads it with `git rev-parse HEAD`. Anything other than 40 hexadecimal characters fails the open with `git rev-parse HEAD gave an unexpected commit ID`.
- `RepositoryScanRunner` keeps the commit ID once the workspace is open, and saves it in every completion after that:

| How the scan run ends | `CommitSha` |
|---|---|
| Completed or PartiallyCompleted | The clone's commit ID |
| Failed after the clone: every scanner failed, the time limit, or the job stopping | The clone's commit ID |
| Failed in the clone | Empty, as `ScanRunCompletion` already says |
| `--path` | Empty |

## The clone time limit (#40)

- `AzureDevOpsOptions` gets `CloneTimeoutMinutes`, `[Range(1, 1440)]`, 8 by default.
- `GitProcess.RunAsync` gets a `timeLimit`. The clone uses `CloneTimeoutMinutes`; `rev-parse` uses 1 minute.
- On reaching it, git and its child processes are stopped, the folder is deleted, and the source throws `TimeoutException` with `The clone took longer than 8 minutes and was stopped (Scanner:AzureDevOps:CloneTimeoutMinutes)`. `RepositoryScanRunner` records that as the scan run's error, and the message is retried.
- The per-repository limit and the job stopping still cancel the token, which stops git the same way (#37).

## Settings

| Setting | Default | `Development` | Rules |
|---|---|---|---|
| `Scanner:AzureDevOps:OrganizationUrl` | | `https://dev.azure.com/BlacklightInternalsMo` | Required in queue mode. An `https` URL with no user name; `file` in tests |
| `Scanner:AzureDevOps:Pat` | | | Required in queue mode. Locally the user environment variable `Scanner__AzureDevOps__Pat`; in Azure a Key Vault secret reference |
| `Scanner:AzureDevOps:CloneTimeoutMinutes` | `8` | | 1 to 1440 |
| ~~`Scanner:Source:LocalRoot`~~ | | | Removed |

- `AzureDevOpsOptions` is a new group (ADR-022), validated at start-up and registered only in queue mode, like `QueueOptions` and `ExecutionOptions`. `--path` needs none of them.
- A new machine missing both the PAT and the database connection string reports both on one line and exits 2, as today.

## Local setup

1. In BlacklightInternalsMo, open **User settings**, then **Personal access tokens**, then **New Token**.
2. Choose organisation **BlacklightInternalsMo** (not "All accessible organizations"), scope **Code: Read**, and a short expiry.
3. Save it as a user environment variable, then open a new terminal or restart Visual Studio or VS Code:

   ```bash
   setx Scanner__AzureDevOps__Pat "<the token>"
   ```

4. Start Azurite, as in Issue 7.
5. Add a request: `.\scripts\enqueue-local.ps1 -RepositoryName victim-repo`. #41 changes the script's defaults to project `Kudos Wall` and branch `main`.
6. Run `dotnet run --project src/RepoScanner.Scanner`.
7. In SSMS, the scan run is Completed, its `CommitSha` starts `b653342`, and the findings are saved.

## README

- **Prerequisites:** Git 2.31 or later on PATH, for `GIT_CONFIG_COUNT`.
- **Configuration:** the `Scanner:AzureDevOps` settings; `Scanner:Source:LocalRoot` removed; the line saying the Azure DevOps settings come in Issue 8 removed.
- **Scan from the queue locally:** the PAT steps in place of the `LocalRoot` steps, with `victim-repo` in `Kudos Wall` as the example, including the Storage Explorer message. What a rejected PAT looks like, and that it stops the run.
- **Exit codes:** 1 now also means Azure DevOps refused the PAT.
- **Known limitations:** the limits below, and the Issue 7 line about local folders removed.

## Testing

Each task is written test first. `dotnet test` still needs no network, no Azurite and no SQL Server, but it does need git, as the scanner does.

`BareRepository`, a test helper, makes a working repository in a temporary folder, commits the given files in two commits, and clones it bare to `<root>/<project>/_git/<repository>`. `file:///<root>` is then the `OrganizationUrl`, and the real `CloneUrl` finds it, including in a project named `Kudos Wall`. Fake PATs in tests start with `your-`, which RepoScanner's own self-scan treats as a placeholder (Issue 3 design, Placeholder allow-list).

| Task | Tests |
|---|---|
| #39 | `Create` gives a new empty folder inside the root, a different one each time, and creates a missing root; `Delete` removes a clone with read-only pack files, refuses a folder outside the root, and logs a warning instead of throwing when it cannot delete |
| #37 | `AzureDevOpsOptions`: each rule, a missing PAT naming `Scanner__AzureDevOps__Pat`, no failure message containing the value, `ToString` hiding it. `CloneUrl`: `Kudos Wall` escaped, a trailing `/` ignored. `GitCommand.Clone`: the PAT and its Base64 in no argument, the header in `GIT_CONFIG_VALUE_0`, `credential.helper=` and `--` present, prompts off. `GitProcess`: the started process's arguments and environment are exactly the command's; output returned; a non-zero exit throws with the last lines and every secret wiped; cancelling stops git. `AzureDevOpsCloneSource` against a bare repository: the branch is cloned with only its latest commit (`rev-list --count` gives 1); a missing branch or repository fails and leaves no folder; git's rejected-token wording becomes `AzureDevOpsAuthenticationException`, and other wording does not. `ScanOrchestrator`: a rejected PAT stops the loop after that message, leaves it and the next one, and logs why. `Program`: the source is `AzureDevOpsCloneSource`; a missing PAT exits 2 with one line; a rejected PAT exits 1 with one line. Every existing `--path` test passes unchanged |
| #38 | `RepositoryScanRunner` saves the commit ID on Completed, PartiallyCompleted, and a failure after the clone, and none when opening fails; the source gives the bare repository's latest commit ID; `--path` saves none |
| #40 | The default and range of `CloneTimeoutMinutes`; a git command that does not finish (a `!sleep 60` alias) is stopped at the limit, with its child process, and throws `TimeoutException`; a clone stopped this way leaves no folder |
| #41 | `QueueModeTests` through `Program`, with the source cloning a bare copy of the secrets fixture: criterion 1, a message gives a Completed scan run with the fixture's findings and the bare repository's commit ID; criterion 2, nothing the program printed contains the PAT or its Base64; criterion 3, the workspace root is empty after a completed scan and after a failed one. Issue 7's acceptance tests keep passing with the clone in place of `LocalRoot` |

- The 80% line coverage gate on the Scanner project covers the new classes.
- **Manual checks** against BlacklightInternalsMo, with Azurite and SSMS:
  1. `victim-repo`: Completed, its findings, `CommitSha` starting `b653342`, and no folder left in `%TEMP%\reposcanner`.
  2. A wrong PAT, set for one terminal: Failed with `Azure DevOps refused the token ...`, exit 1, the message still on the queue, and no sign-in window.
  3. `-RepositoryName ghost`: Failed with git's "does not exist" reason, and the message retried.

## Order of work

One task at a time, stopping after each for review, then a commit whose message starts "This commit" and ends in its task number (ADR-020):

1. **#36** This design document.
2. **#39** `WorkspaceFolders`: a new folder per clone, always deleted.
3. **#37** `AzureDevOpsOptions`, `CloneUrl`, `GitCommand`, `GitProcess`, `AzureDevOpsCloneSource` and the rejected-PAT stop; `LocalFolderSource` and `SourceOptions` removed with their tests; the ADR-016 and ADR-017 amendments; deleting the `Scanner__Source__LocalRoot` user environment variable from the developer's machine.
4. **#38** The commit ID on the scan run.
5. **#40** The clone time limit.
6. **#41** The acceptance tests through `Program`, `enqueue-local.ps1`'s defaults, the README, and the manual checks.

## Not in Issue 8

| Item | Where it is on the board |
|---|---|
| Git in the scanner image, and docker-compose passing `Scanner__AzureDevOps__Pat` to the container | Issue 9 |
| The Key Vault secret, its reference on the jobs, and `OrganizationUrl` on the jobs | The deployment Issues |
| The enqueuer, which shares the PAT | Not in this repository |
| An alert on a rejected PAT | Issue 10: it watches the `Azure DevOps refused the token` log line |
| Scanning history or other branches | Not on the board: ADR-016's Could-have, out of scope for the EPA |
| A managed identity in place of the PAT | Not on the board: ADR-007's production upgrade |

## Known limitations

- Only the latest commit of the default branch is scanned. A secret committed and later removed stays in history and is not found (ADR-016).
- One organisation per scanner deployment.
- The PAT acts as the person who created it: it sees only their repositories, stops working if their account is removed, and expires within a year, so it needs renewing.
- While git runs, the PAT is in its process's environment, which another program running as the same user can read.
- A rejected PAT is recognised from git's English error text, so a git that prints another language or other wording gives an ordinary failed scan instead.
- If the scanner process is killed outright, its folder in `<temp>/reposcanner` is left behind. In Azure the replica's disk goes with it; locally it stays until deleted.
- A repository renamed since its message was sent fails to clone, as the Issue 7 design expected. The next nightly message has the new name.
