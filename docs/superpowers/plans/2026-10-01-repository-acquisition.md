# Issue 8: Repository acquisition, implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** In queue mode the scanner clones the latest commit of the message's repository and branch from Azure DevOps into a new temporary folder, scans it, saves the commit ID on the scan run, and deletes the folder, with the PAT never in git's arguments or any log.

**Architecture:** `WorkspaceFolders` makes and deletes one folder per clone. `GitCommand` builds a git command (arguments, environment variables, secrets to wipe) and `GitProcess` runs it, stopping git and its child processes on cancellation or a time limit. `AzureDevOpsCloneSource` fills Issue 7's `IRepositorySource` slot with them, replacing `LocalFolderSource`; a rejected PAT becomes `AzureDevOpsAuthenticationException`, which the queue loop and `Program` turn into a stopped run with exit 1. `RepositoryWorkspace` carries the commit ID to `RepositoryScanRunner`.

**Tech Stack:** .NET 10, Generic Host, `System.Diagnostics.Process`, git 2.31 or later on PATH, NUnit 4.6, `FakeLogger`, `FakeTimeProvider`, in-memory SQLite. No new packages.

**Spec:** [docs/design/2026-10-01-repository-acquisition.md](../../design/2026-10-01-repository-acquisition.md). Read it before starting; this plan argues from it. The design itself was committed as `f609bf8` (#36).

## Global Constraints

- Branch: `feature/36-repository-acquisition` (already created from `develop`).
- **Commits:** one per task, only after the user has reviewed that task's diff. The message starts `This commit` and ends with the task number, for example `#39`. It never mentions Claude, Opus or any AI tool: no `Co-Authored-By` line.
- **Board:** set the task to Doing when its plan task starts and to Done when its commit is made, with `az boards work-item update --org https://dev.azure.com/Blacklight-Internal --id <id> --state <Doing|Done>`. Tasks 2 and 3 are both #37: #37 goes to Done after Task 3's commit. Issue #36 stays Doing until its pull request is merged. Push and open a pull request only when the user asks.
- ADR-021 style, enforced by the build: explicit `using` directives (System first, then alphabetical), block-scoped namespaces, explicit types (no `var`), `sealed` or `static` classes, `TreatWarningsAsErrors`. Zero warnings. Every non-obvious line has a comment giving the reason, in British English.
- No em dash (U+2014) in any file.
- **The PAT never appears** in git's arguments, a URL, a file, a log line or an exception message. Fake PATs in tests are `your-test-pat-0001`, which the self-scan's placeholder allow-list skips. Leak checks use `Assert.That(leaked, Is.False, "...")`, never `Does.Not.Contain(pat)`.
- Exact text from the spec:
  - Missing PAT: `Scanner:AzureDevOps:Pat is required in queue mode. Locally, set the Scanner__AzureDevOps__Pat user environment variable to a personal access token with Code (Read).`
  - Bad organisation URL: `Scanner:AzureDevOps:OrganizationUrl must be an https URL such as https://dev.azure.com/<organisation>, with no user name in it`
  - Rejected PAT: `Azure DevOps refused the token in Scanner:AzureDevOps:Pat for <OrganizationUrl>. Check that it has not expired or been revoked, and that it has Code (Read).`
  - Program's line: `Azure DevOps refused the token, scanner stopped: {Problem}`; the loop's stop reason: `Azure DevOps refused the token`
  - Clone limit: `The clone took longer than 8 minutes and was stopped (Scanner:AzureDevOps:CloneTimeoutMinutes)`
  - Commit ID: `git rev-parse HEAD gave an unexpected commit ID`
- Git failure messages are `git <name> failed with exit code <code>: <the last 3 non-empty lines of standard error, joined by a space>`, every secret replaced by `***`.
- Directory walks set `AttributesToSkip = FileAttributes.ReparsePoint`: the default skips Hidden and System, and `.git` is Hidden on Windows.
- Tests: NUnit 4, `Method_Condition_Result` names, `Assert.That`, `using (Assert.EnterMultipleScope())` for related assertions. Tests need git on PATH but no network, Azurite or SQL Server.
- **Filtered test runs add `-p:CollectCoverage=false`**, because the 80% gate counts only the tests that ran. The full `dotnet test RepoScanner.sln` at the end of each task checks coverage.

## Review Focus

Inputs the spec does not mention, most likely to bite a person first. Each has a test in the task named.

1. **Project or repository names with characters other than a space**, such as `R&D #1` or `Café+api`: escaped, so git asks Azure DevOps for the right repository. Task 2: `For_EscapesEveryCharacterAUrlCannotHold`.
2. **A temporary folder whose path has a space**, as a Windows user called `Jo Smith` has: the clone, the scan and the delete still work. Task 3: `OpenAsync_WithASpaceInTheWorkspacePath_Clones`.
3. **Paths longer than 260 characters** once inside `%TEMP%\reposcanner\<name>` on Windows: cloned (`core.longpaths`) and deleted. Task 3: `OpenAsync_WithAPathLongerThan260Characters_ClonesAndDeletes`.
4. **An empty repository**, as a new Azure DevOps repository is: an ordinary failed clone with git's reason and no folder left, not a crash. Task 3: `OpenAsync_ForAnEmptyRepository_FailsWithGitsReasonAndLeavesNoFolder`.
5. **A developer with `GIT_TRACE_CURL` or `GIT_CURL_VERBOSE` set**, which makes git print its request headers: the PAT still never reaches an exception or a log. Task 3: `OpenAsync_WithGitTracingOn_NeverReportsThePat`.

---

### Task 1: WorkspaceFolders (#39)

**Files:**
- Create: `src/RepoScanner.Scanner/Sources/WorkspaceFolders.cs`
- Modify: `tests/RepoScanner.Scanner.Tests/TempFolder.cs` (`Dispose`)
- Test: `tests/RepoScanner.Scanner.Tests/Sources/WorkspaceFoldersTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `namespace RepoScanner.Scanner.Sources`; `public sealed class WorkspaceFolders { public WorkspaceFolders(string root, ILogger<WorkspaceFolders> logger); public string Root { get; } public string Create(); public void Delete(string folder); }`. `Root` is the full path, with no trailing separator.

- [ ] **Step 1: Set #39 to Doing**

- [ ] **Step 2: Write the failing tests**

`WorkspaceFoldersTests`, with a `TempFolder _temp`, a `FakeLogger<WorkspaceFolders> _logger` and `Folders()` returning `new WorkspaceFolders(Path.Combine(_temp.FullPath, "reposcanner"), _logger)`, so the root does not exist until the first `Create`:

```csharp
[Test]
public void Create_GivesANewEmptyFolderDirectlyInsideTheRoot()
{
    WorkspaceFolders folders = Folders();

    string folder = folders.Create();

    using (Assert.EnterMultipleScope())
    {
        Assert.That(Directory.Exists(folder), Is.True);
        Assert.That(Path.GetDirectoryName(folder), Is.EqualTo(folders.Root));
        Assert.That(Path.GetFileName(folder), Does.Match("^[0-9a-f]{12}$"));
        Assert.That(Directory.EnumerateFileSystemEntries(folder), Is.Empty);
    }
}

[Test]
public void Create_Twice_GivesTwoDifferentFolders()
// Assert.That(folders.Create(), Is.Not.EqualTo(folders.Create()));

// Decision 11: git marks its pack files read-only on Windows, and Directory.Delete then fails
[Test]
public void Delete_RemovesTheFolderEvenWithReadOnlyFilesInside()
// Writes <folder>/.git/objects/pack/pack-1.pack, marks it FileAttributes.ReadOnly, and the .git folder Hidden.
// Assert.That(Directory.Exists(folder), Is.False); Assert.That(_logger.Collector.Count, Is.Zero);

// A link in a cloned repository must never change a file outside it
[Test]
public void Delete_DoesNotFollowALinkToAFolderOutside()
// _temp.WriteText("outside/keep.txt", "keep"), marked ReadOnly. Directory.CreateSymbolicLink(<folder>/link, <outside>);
// an IOException or UnauthorizedAccessException from CreateSymbolicLink calls
// Assert.Ignore("Symbolic links need Developer Mode or administrator rights on this machine").
// After Delete: folder gone, keep.txt still exists and still ReadOnly.

[Test]
public void Delete_ForAFolderNotDirectlyInsideTheRoot_LeavesItAndWarns()
// other = <_temp>/elsewhere. Directory.Exists(other) is True;
// _logger.LatestRecord.Level is LogLevel.Warning and its Message starts "Refused to delete".

[Test, Platform(Include = "Win")]
public void Delete_WhenAFileIsLocked_WarnsInsteadOfThrowing()
// Holds <folder>/locked.txt open with FileShare.None; Assert.DoesNotThrow(() => folders.Delete(folder));
// _logger.LatestRecord.Message starts "Could not delete".
```

- [ ] **Step 3: Run them to see them fail**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~WorkspaceFoldersTests" -p:CollectCoverage=false`
Expected: build fails, `WorkspaceFolders` does not exist.

- [ ] **Step 4: Implement `WorkspaceFolders`**

- Constructor: `Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))`.
- `Create()`: `Directory.CreateDirectory(Root)`, then a name of `Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant()`, trying another in the unlikely case it exists; returns the new folder's full path.
- `Delete(folder)`: if `Path.GetDirectoryName(Path.GetFullPath(folder))` is not `Root` (ignoring case), log `Refused to delete {Folder}: it is not directly inside {Root}` as a warning and return. Otherwise clear `ReadOnly` on every file found with `new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }`, then `Directory.Delete(folder, recursive: true)`. An `IOException` or `UnauthorizedAccessException` logs `Could not delete {Folder}: {Reason}` as a warning. A folder already gone does nothing.

- [ ] **Step 5: Make `TempFolder.Dispose` clear read-only attributes first**

The same walk as step 4, so a test folder holding a git repository (from Task 3) can be deleted on Windows.

- [ ] **Step 6: Run the tests to see them pass**

Run the step 3 command. Expected: all pass (`Delete_DoesNotFollowALinkToAFolderOutside` may be ignored without Developer Mode).

- [ ] **Step 7: Full build and tests**

Run: `dotnet build RepoScanner.sln` (zero warnings) and `dotnet test RepoScanner.sln`. Expected: all pass, Core and Scanner at 80% or more.

- [ ] **Step 8: Stop for review, then commit and set #39 to Done**

```bash
git add src/RepoScanner.Scanner/Sources/WorkspaceFolders.cs tests/RepoScanner.Scanner.Tests/Sources/WorkspaceFoldersTests.cs tests/RepoScanner.Scanner.Tests/TempFolder.cs
git commit -m "This commit adds WorkspaceFolders, which makes a new folder with a random 12-character name under one root for each clone and deletes it after clearing read-only attributes, without following links, refusing a folder outside the root and only warning when a delete fails; and makes TempFolder clear read-only attributes before deleting #39"
```

---

### Task 2: Settings, the clone URL, and running git (#37, part 1)

**Files:**
- Create: `src/RepoScanner.Scanner/Configuration/AzureDevOpsOptions.cs`
- Create: `src/RepoScanner.Scanner/Sources/CloneUrl.cs`
- Create: `src/RepoScanner.Scanner/Git/GitCommand.cs`, `GitCommandException.cs`, `GitProcess.cs`
- Test: `tests/RepoScanner.Scanner.Tests/Configuration/AzureDevOpsOptionsTests.cs`, `tests/RepoScanner.Scanner.Tests/Sources/CloneUrlTests.cs`, `tests/RepoScanner.Scanner.Tests/Git/GitCommandTests.cs`, `tests/RepoScanner.Scanner.Tests/Git/GitProcessTests.cs`

**Interfaces:**
- Produces:
  - `namespace RepoScanner.Scanner.Configuration`: `public sealed record AzureDevOpsOptions : IValidatableObject { public const string SectionName = "Scanner:AzureDevOps"; public string OrganizationUrl { get; init; } = string.Empty; [Required(ErrorMessage = <missing PAT text>)] public string Pat { get; init; } = string.Empty; }`, whose `ToString()` prints `Pat = ***`.
  - `namespace RepoScanner.Scanner.Sources`: `public static class CloneUrl { public static string For(string organizationUrl, string projectName, string repositoryName); }`
  - `namespace RepoScanner.Scanner.Git`:
    - `public sealed record GitCommand(string Name, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment, IReadOnlyList<string> Secrets) { public static GitCommand Clone(string url, string branch, string folder, string pat); }`, whose `ToString()` prints the name and arguments only. `Name` (`clone`, `rev-parse`) is added to the spec's record so failure messages can say `git clone failed ...` without guessing it from the arguments.
    - `public sealed class GitCommandException : Exception { public GitCommandException(string commandName, int exitCode, string standardError); public int ExitCode { get; } public string StandardError { get; } }`; `StandardError` is the last 20 lines, secrets already wiped.
    - `public sealed class GitProcess { public GitProcess(ILogger<GitProcess> logger); public Task<string> RunAsync(GitCommand command, CancellationToken cancellationToken); internal static ProcessStartInfo CreateStartInfo(GitCommand command); }`

- [ ] **Step 1: Set #37 to Doing**

- [ ] **Step 2: Write the failing tests**

`AzureDevOpsOptionsTests`, using `DataAnnotationValidateOptions<AzureDevOpsOptions>(null)` as `ExecutionOptionsTests` does, and `private const string Pat = "your-test-pat-0001";`:

```csharp
[TestCase("https://dev.azure.com/BlacklightInternalsMo")]
[TestCase("https://dev.azure.com/BlacklightInternalsMo/")]
[TestCase("file:///C:/repos")]
public void Validate_WithAnHttpsOrFileUrlAndAPat_Succeeds(string organizationUrl)

[Test]
public void Validate_WithoutAPat_SaysWhatToSet()
// FailureMessage contains "Scanner:AzureDevOps:Pat" and "Scanner__AzureDevOps__Pat"

[TestCase("")]
[TestCase("dev.azure.com/BlacklightInternalsMo")]
[TestCase("http://dev.azure.com/BlacklightInternalsMo")]
[TestCase("ftp://dev.azure.com/BlacklightInternalsMo")]
// The address Azure DevOps' Clone dialog offers has the user name in it, which is the pattern this tool flags
[TestCase("https://BlacklightInternalsMo@dev.azure.com/BlacklightInternalsMo")]
public void Validate_WithABadOrganizationUrl_FailsWithoutShowingThePat(string organizationUrl)
// FailureMessage contains the bad organisation URL text; bool leaked = FailureMessage.Contains(Pat); Assert.That(leaked, Is.False, ...)

[Test]
public void ToString_HidesThePat()
// Contains "Pat = ***"; leaked is False

// ADR-007: no appsettings file ever holds the PAT
[Test]
public void AppSettingsFiles_HaveNoPat()
// As DatabaseRegistrationTests.AppSettingsFiles_HaveNoDatabaseSection, for the key "Scanner:AzureDevOps:Pat"
```

`CloneUrlTests`:

```csharp
[TestCase("https://dev.azure.com/BlacklightInternalsMo", "Kudos Wall", "victim-repo", "https://dev.azure.com/BlacklightInternalsMo/Kudos%20Wall/_git/victim-repo")]
[TestCase("https://dev.azure.com/BlacklightInternalsMo/", "Kudos Wall", "victim-repo", "https://dev.azure.com/BlacklightInternalsMo/Kudos%20Wall/_git/victim-repo")]
public void For_BuildsTheAzureDevOpsCloneUrl(string organizationUrl, string projectName, string repositoryName, string expected)

// Review Focus 1
[Test]
public void For_EscapesEveryCharacterAUrlCannotHold()
// For("https://dev.azure.com/org", "R&D #1", "Café+api") is "https://dev.azure.com/org/R%26D%20%231/_git/Caf%C3%A9%2Bapi"
```

`GitCommandTests`, with `Url = "https://dev.azure.com/BlacklightInternalsMo/Kudos%20Wall/_git/victim-repo"`, `Folder = Path.Combine(Path.GetTempPath(), "reposcanner", "3f9c0a1b7d2e")`, and `Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + Pat))`:

```csharp
[Test]
public void Clone_HasTheArgumentsOfTheDesign()
{
    GitCommand command = GitCommand.Clone(Url, "main", Folder, Pat);

    using (Assert.EnterMultipleScope())
    {
        Assert.That(command.Name, Is.EqualTo("clone"));
        Assert.That(command.Arguments, Is.EqualTo(new string[]
        {
            "-c", "credential.helper=", "-c", "core.longpaths=true",
            "clone", "--depth", "1", "--single-branch", "--branch", "main", "--", Url, Folder
        }));
    }
}

// Success criterion 2 and decision 1: the PAT is in git's environment, never its arguments
[Test]
public void Clone_PutsThePatOnlyInTheEnvironment()
{
    GitCommand command = GitCommand.Clone(Url, "main", Folder, Pat);
    bool leaked = command.Arguments.Any(argument => argument.Contains(Pat, StringComparison.Ordinal) || argument.Contains(Base64, StringComparison.Ordinal));

    using (Assert.EnterMultipleScope())
    {
        Assert.That(leaked, Is.False, "the PAT is in git's arguments");
        Assert.That(command.Environment["GIT_CONFIG_COUNT"], Is.EqualTo("1"));
        Assert.That(command.Environment["GIT_CONFIG_KEY_0"], Is.EqualTo("http.extraheader"));
        Assert.That(command.Environment["GIT_CONFIG_VALUE_0"], Is.EqualTo("AUTHORIZATION: Basic " + Base64));
        Assert.That(command.Secrets, Is.EquivalentTo(new string[] { Pat, Base64 }));
    }
}

// Decision 8
[Test]
public void Clone_TurnsOffPrompts()
// Environment["GIT_TERMINAL_PROMPT"] is "0" and Environment["GCM_INTERACTIVE"] is "never"

[Test]
public void ToString_ShowsNoSecrets()
// leaked = ToString() contains Pat or Base64; Is.False
```

`GitProcessTests`, with `Git()` returning `new GitProcess(new FakeLogger<GitProcess>())` and `Command(name, params string[] arguments)` building a `GitCommand` with the prompts-off environment and `Secrets = { Pat }`:

```csharp
// The arguments go through ArgumentList, so nothing is re-quoted, and only the command's variables are added
[Test]
public void CreateStartInfo_UsesTheCommandsArgumentsAndEnvironment()
// FileName "git"; ArgumentList Is.EqualTo(command.Arguments); Arguments Is.Empty; UseShellExecute False;
// RedirectStandardInput, RedirectStandardOutput and RedirectStandardError True; each command.Environment
// entry is in info.Environment with the same value

[Test]
public async Task RunAsync_ReturnsWhatGitPrinted()
// await Git().RunAsync(Command("version", "--version"), CancellationToken.None) starts with "git version"

[Test]
public void RunAsync_WhenGitFails_ThrowsWithItsExitCodeAndLastLinesWithSecretsWiped()
{
    GitCommand failing = Command("fail", "-c", "alias.fail=!echo first line >&2; echo " + Pat + " >&2; exit 3", "fail");

    GitCommandException exception = Assert.ThrowsAsync<GitCommandException>(() => Git().RunAsync(failing, CancellationToken.None));
    bool leaked = exception.Message.Contains(Pat, StringComparison.Ordinal) || exception.StandardError.Contains(Pat, StringComparison.Ordinal);

    using (Assert.EnterMultipleScope())
    {
        Assert.That(exception.ExitCode, Is.EqualTo(3));
        Assert.That(exception.Message, Is.EqualTo("git fail failed with exit code 3: first line ***"));
        Assert.That(leaked, Is.False, "the PAT was not wiped");
    }
}

// git starts helper processes; if one outlived git, its open output pipe would keep RunAsync waiting
[Test]
public void RunAsync_WhenCancelled_StopsGitAndItsChildQuickly()
// alias "!sleep 60"; CancellationTokenSource.CancelAfter(500 ms); throws OperationCanceledException
// (Throws.InstanceOf); a Stopwatch shows less than 15 seconds
```

- [ ] **Step 3: Run them to see them fail**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~AzureDevOpsOptionsTests|FullyQualifiedName~CloneUrlTests|FullyQualifiedName~GitCommandTests|FullyQualifiedName~GitProcessTests" -p:CollectCoverage=false`
Expected: build fails, the types do not exist.

- [ ] **Step 4: Implement `AzureDevOpsOptions`**

`Validate` yields the bad organisation URL text (member `OrganizationUrl`) unless `Uri.TryCreate(OrganizationUrl, UriKind.Absolute, out Uri? uri)` gives scheme `https` or `file` and an empty `UserInfo`. Override `PrintMembers(StringBuilder builder)` to print `OrganizationUrl = ..., Pat = ***`.

- [ ] **Step 5: Implement `CloneUrl.For`**

`organizationUrl.TrimEnd('/') + "/" + Uri.EscapeDataString(projectName) + "/_git/" + Uri.EscapeDataString(repositoryName)`.

- [ ] **Step 6: Implement `GitCommand`, `GitCommandException` and `GitProcess`**

- `GitCommand.Clone` builds the arguments, the environment (`GIT_TERMINAL_PROMPT=0`, `GCM_INTERACTIVE=never`, then the three `GIT_CONFIG_*`) and `Secrets = { pat, base64 }`. `ToString` is overridden to `git <name> <arguments>`.
- `GitCommandException`'s message is the git failure message of the Global Constraints.
- `CreateStartInfo`: `FileName = "git"`, `UseShellExecute = false`, `CreateNoWindow = true`, the three redirects, `ArgumentList` and `Environment` from the command.
- `RunAsync`: starts the process, closes standard input at once so git can never wait on it, reads both outputs concurrently (keeping the last 20 lines of standard error), and awaits `WaitForExitAsync(cancellationToken)`. On cancellation, `Kill(entireProcessTree: true)`, waits for exit, then rethrows. After exit, awaits both readers, wipes every non-empty secret with `***`, and throws `GitCommandException` on a non-zero exit code; otherwise returns standard output.

- [ ] **Step 7: Run the tests to see them pass**

Run the step 3 command. Expected: all pass.

- [ ] **Step 8: Full build and tests**

`dotnet build RepoScanner.sln` (zero warnings), then `dotnet test RepoScanner.sln`. Expected: all pass, coverage gate met.

- [ ] **Step 9: Stop for review, then commit (#37 stays Doing)**

```bash
git add src/RepoScanner.Scanner/Configuration/AzureDevOpsOptions.cs src/RepoScanner.Scanner/Sources/CloneUrl.cs src/RepoScanner.Scanner/Git tests/RepoScanner.Scanner.Tests/Configuration/AzureDevOpsOptionsTests.cs tests/RepoScanner.Scanner.Tests/Sources/CloneUrlTests.cs tests/RepoScanner.Scanner.Tests/Git
git commit -m "This commit adds AzureDevOpsOptions, with the organisation URL and the PAT, which ToString hides; CloneUrl, which escapes the project and repository names; GitCommand, which builds the shallow single-branch clone with the PAT only in GIT_CONFIG_* environment variables, the sign-in helper switched off and prompts off; and GitProcess, which runs git with ArgumentList, wipes secrets from what it prints, and stops git and its child processes when cancelled #37"
```

---

### Task 3: The clone source in place of LocalFolderSource (#37, part 2)

**Files:**
- Create: `src/RepoScanner.Scanner/Sources/AzureDevOpsCloneSource.cs`, `src/RepoScanner.Scanner/Sources/AzureDevOpsAuthenticationException.cs`
- Delete: `src/RepoScanner.Scanner/Sources/LocalFolderSource.cs`, `src/RepoScanner.Scanner/Configuration/SourceOptions.cs`, `tests/RepoScanner.Scanner.Tests/Sources/LocalFolderSourceTests.cs`, `tests/RepoScanner.Scanner.Tests/Configuration/SourceOptionsTests.cs`
- Modify: `src/RepoScanner.Scanner/ScanOrchestrator.cs` (`RunAsync`, `HandleAsync`'s catch), `src/RepoScanner.Scanner/Program.cs` (registrations at lines 235 to 259, a new catch, the comment at line 109), `src/RepoScanner.Scanner/Queue/ScanRequestReader.cs:163` (comment), `src/RepoScanner.Scanner/appsettings.Development.json`
- Modify: `docs/adr/ADR-016-shallow-single-branch-clone-with-the-credential-in-a-header.md`, `docs/adr/ADR-017-everything-runs-locally-before-azure.md`, `docs/adr/README.md`
- Create (tests): `tests/RepoScanner.Scanner.Tests/Sources/BareRepository.cs`, `FakeAzureDevOps.cs`, `FakeRepositorySource.cs`, `AzureDevOpsCloneSourceTests.cs`
- Modify (tests): `ScanOrchestratorTests.cs`, `ProgramTests.cs`, `QueueModeTests.cs`, `Configuration/DatabaseRegistrationTests.cs`, `Configuration/AzureDevOpsOptionsTests.cs`

**Interfaces:**
- Consumes: `WorkspaceFolders` (Task 1); `AzureDevOpsOptions`, `CloneUrl`, `GitCommand.Clone`, `GitProcess.RunAsync(GitCommand, CancellationToken)`, `GitCommandException` (Task 2).
- Produces:
  - `public sealed class AzureDevOpsCloneSource : IRepositorySource { public AzureDevOpsCloneSource(IOptions<AzureDevOpsOptions> options, GitProcess git, WorkspaceFolders folders, ILogger<AzureDevOpsCloneSource> logger); internal static bool IsRejectedToken(string standardError); }`
  - `public sealed class AzureDevOpsAuthenticationException : Exception { public AzureDevOpsAuthenticationException(string message, Exception innerException); }`
  - Test helpers in `RepoScanner.Scanner.Tests.Sources`:
    - `internal static class BareRepository { public static string Create(string root, string projectName, string repositoryName, string branch, string contentFolder); public static void CreateEmpty(string root, string projectName, string repositoryName); public static string OrganizationUrl(string root); public static string Git(string folder, params string[] arguments); }`. `Create` makes `<root>/<project>/_git/<repository>` with two commits on `branch` (the first adds `first.txt`, the second every file in `contentFolder`) and returns the second commit's ID. `OrganizationUrl` is `new Uri(root).AbsoluteUri`. `Git` runs git with `GIT_CONFIG_NOSYSTEM=1`, `GIT_CONFIG_GLOBAL` set to an empty file, `-c user.name=RepoScanner Tests -c user.email=tests@example.com -c core.longpaths=true`, and `add --all --force` / `commit --no-verify`, so the developer's own git settings (signing, hooks, ignore rules) cannot change a test. It returns standard output and throws on failure.
    - `internal sealed class FakeAzureDevOps : IDisposable { public static FakeAzureDevOps StartRefusing(); public static FakeAzureDevOps StartStalling(); public string OrganizationUrl { get; } public IReadOnlyList<string> AuthorizationHeaders { get; } public Task FirstRequest { get; } }`: a `TcpListener` on `127.0.0.1` port 0. Refusing answers every request `HTTP/1.1 401 Unauthorized` with `WWW-Authenticate: Basic realm="Azure DevOps"`; stalling accepts and never answers. It records each request's `Authorization` header value.
    - `internal sealed class FakeRepositorySource : IRepositorySource { public FakeRepositorySource(string root); public Exception? Failure { get; set; } }`: throws `Failure` when set; otherwise opens `<root>/<repositoryName>` with `RepositoryWorkspace.ForExistingFolder`, or throws `DirectoryNotFoundException("No folder named <name> in <root>")`, as `LocalFolderSource` did.

- [ ] **Step 1: Write the failing clone source tests**

`AzureDevOpsCloneSourceTests`, with `TempFolder _repos` (the bare repositories), `TempFolder _temp` (content and workspaces), `WorkspaceFolders _folders` rooted at `<_temp>/reposcanner`, a `FakeLogger<AzureDevOpsCloneSource> _logger`, `Source(string organizationUrl)` building the source from `Options.Create(new AzureDevOpsOptions { OrganizationUrl = organizationUrl, Pat = Pat })`, and `Request(project, repository, branch)`:

```csharp
[Test]
public async Task OpenAsync_ClonesTheBranchWithOnlyItsLatestCommit()
{
    string content = _temp.WriteText("content/README.md", "second commit");
    BareRepository.Create(_repos.FullPath, "Kudos Wall", "victim-repo", "main", Path.GetDirectoryName(content)!);

    await using (RepositoryWorkspace workspace = await Source(BareRepository.OrganizationUrl(_repos.FullPath))
        .OpenAsync(Request("Kudos Wall", "victim-repo", "main"), CancellationToken.None))
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Path.GetDirectoryName(workspace.Folder), Is.EqualTo(_folders.Root));
            Assert.That(File.ReadAllText(Path.Combine(workspace.Folder, "README.md")), Does.StartWith("second commit"));
            Assert.That(BareRepository.Git(workspace.Folder, "rev-list", "--count", "HEAD").Trim(), Is.EqualTo("1"));
        }
    }
}

[Test] public async Task OpenAsync_ClosingTheWorkspace_DeletesTheClone()
// After DisposeAsync, Directory.Exists(workspace.Folder) is False

// Success criterion 2: no file holds the PAT either
[Test] public async Task OpenAsync_NeverWritesThePatIntoTheClone()
// leaked = <clone>/.git/config contains Pat or its Base64; Is.False

[Test] public void OpenAsync_ForAMissingBranch_FailsWithGitsReasonAndLeavesNoFolder()
// Branch "release": GitCommandException whose Message starts "git clone failed with exit code 128:";
// Directory.EnumerateFileSystemEntries(_folders.Root) Is.Empty

[Test] public void OpenAsync_ForAMissingRepository_FailsAndLeavesNoFolder()
// Repository "ghost": GitCommandException; the root is empty

// Review Focus 4
[Test] public void OpenAsync_ForAnEmptyRepository_FailsWithGitsReasonAndLeavesNoFolder()
// BareRepository.CreateEmpty; GitCommandException (not AzureDevOpsAuthenticationException); the root is empty

// Decision 13, against a real git: the server refuses, git cannot ask for a login, and the source names the problem
[Test]
public void OpenAsync_WhenAzureDevOpsRefusesTheToken_ThrowsAuthenticationExceptionAndLeavesNoFolder()
{
    using (FakeAzureDevOps server = FakeAzureDevOps.StartRefusing())
    {
        AzureDevOpsAuthenticationException exception = Assert.ThrowsAsync<AzureDevOpsAuthenticationException>(
            () => Source(server.OrganizationUrl).OpenAsync(Request("Kudos Wall", "victim-repo", "main"), CancellationToken.None));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Is.EqualTo("Azure DevOps refused the token in Scanner:AzureDevOps:Pat for " + server.OrganizationUrl
                + ". Check that it has not expired or been revoked, and that it has Code (Read)."));
            // Decision 1 works end to end: the header from the environment reached the request
            Assert.That(server.AuthorizationHeaders, Has.Some.EqualTo("Basic " + Base64));
            Assert.That(Directory.EnumerateFileSystemEntries(_folders.Root), Is.Empty);
        }
    }
}

// Review Focus 5. Sets GIT_TRACE_CURL=1 and GIT_CURL_VERBOSE=1 for this process, restored in finally.
[Test] public void OpenAsync_WithGitTracingOn_NeverReportsThePat()
// Against StartRefusing: leaked = exception.ToString() (inner exceptions included) or any _logger record's Message
// contains Pat or Base64; Is.False

[Test] public async Task OpenAsync_WhenCancelled_StopsTheCloneAndLeavesNoFolder()
// Against StartStalling: start OpenAsync, await server.FirstRequest (WaitAsync 30 s), cancel the token;
// Throws.InstanceOf<OperationCanceledException>; the root is empty

// Review Focus 2
[Test] public async Task OpenAsync_WithASpaceInTheWorkspacePath_Clones()
// _folders rooted at <_temp>/work space/reposcanner; README.md is in the clone

// Review Focus 3
[Test, Platform(Include = "Win")] public async Task OpenAsync_WithAPathLongerThan260Characters_ClonesAndDeletes()
// Content holds a file whose path inside the clone makes Path.Combine(workspace.Folder, relative).Length above 260;
// it exists in the clone, and after DisposeAsync the clone folder is gone

[Test] public async Task OpenAsync_LogsTheCloneWithFreeSpace()
// A record whose Message starts "Cloned Kudos Wall/victim-repo main in " and contains " MB free on "

[TestCase("fatal: could not read Username for 'https://dev.azure.com/BlacklightInternalsMo/Kudos%20Wall/_git/victim-repo': terminal prompts disabled", true)]
[TestCase("fatal: Authentication failed for 'https://dev.azure.com/BlacklightInternalsMo/Kudos%20Wall/_git/victim-repo/'", true)]
[TestCase("fatal: unable to access 'https://dev.azure.com/x/': The requested URL returned error: 401", true)]
[TestCase("fatal: unable to access 'https://dev.azure.com/x/': The requested URL returned error: 403", true)]
[TestCase("remote: TF401019: The Git repository with name or identifier ghost does not exist or you do not have permissions for the operation you are attempting.", false)]
[TestCase("warning: Could not find remote branch release to clone.", false)]
public void IsRejectedToken_RecognisesOnlyARefusedToken(string standardError, bool expected)
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~AzureDevOpsCloneSourceTests" -p:CollectCoverage=false`
Expected: build fails, `AzureDevOpsCloneSource` and the helpers do not exist. Write `BareRepository`, `FakeAzureDevOps` and `FakeRepositorySource` now, so only the source is missing.

- [ ] **Step 3: Implement `AzureDevOpsAuthenticationException` and `AzureDevOpsCloneSource`**

`OpenAsync` follows the spec's six steps without the commit ID (Task 4 adds it): URL, `_folders.Create()`, free space before (`new DriveInfo(Path.GetPathRoot(folder)!)`), `GitCommand.Clone` run by `GitProcess`, the log line `Cloned {ProjectName}/{RepositoryName} {Branch} in {DurationMs} ms; {FreeMegabytes} MB free on {Drive}, {FreeMegabytesBefore} before`, and `new RepositoryWorkspace(folder, () => { _folders.Delete(folder); return ValueTask.CompletedTask; })`. Any exception after `Create` deletes the folder before it goes on. A `GitCommandException` for which `IsRejectedToken(exception.StandardError)` is true is rethrown as `AzureDevOpsAuthenticationException` with the rejected PAT text, the original as its inner exception. `IsRejectedToken` looks for `terminal prompts disabled`, `Authentication failed`, `returned error: 401` and `returned error: 403`, ignoring case.

- [ ] **Step 4: Run the clone source tests to see them pass**

Run the step 2 command. Expected: all pass.

- [ ] **Step 5: Write the failing loop and program tests**

`ScanOrchestratorTests`: a `FakeRepositorySource _source` over `_root`, created in `SetUp` and passed to `ScanOrchestrator` in place of `LocalFolderSource`. Existing tests stay as they are. Add:

```csharp
// Decision 13: every other repository would fail the same way, so the run stops and leaves the messages
[Test]
public async Task RunAsync_WhenAzureDevOpsRefusesTheToken_StopsLeavingThatMessageAndTheRest()
{
    _queue.Add(Body("orders-api", OrdersApiId));
    _queue.Add(Body("billing-api"));
    _source.Failure = new AzureDevOpsAuthenticationException(
        "Azure DevOps refused the token in Scanner:AzureDevOps:Pat for https://dev.azure.com/example. Check that it has not expired or been revoked, and that it has Code (Read).",
        new InvalidOperationException("git clone failed"));

    await Assert.ThatAsync(() => CreateOrchestrator(new StubScanner()).RunAsync(CancellationToken.None), Throws.TypeOf<AzureDevOpsAuthenticationException>());

    using (ScannerDbContext context = _database.CreateContext())
    using (Assert.EnterMultipleScope())
    {
        Assert.That(_queue.Deleted, Is.Empty);
        Assert.That(_queue.Poisoned, Is.Empty);
        Assert.That(_queue.WaitingCount, Is.EqualTo(1), "the second message was never taken");
        Assert.That(context.ScanRuns.Single().Status, Is.EqualTo(ScanRunStatus.Failed));
        Assert.That(context.ScanRuns.Single().ErrorMessage, Does.StartWith("Azure DevOps refused the token"));
        Assert.That(_logger.LatestRecord.Message,
            Is.EqualTo("Stopped taking messages from scan-nightly: Azure DevOps refused the token. 0 scanned, 1 failed, 0 skipped, 0 poisoned"));
    }
}
```

`ProgramTests`: `ValidArgs` and `DatabaseRegistrationTests.QueueArgs` replace `--Scanner:Source:LocalRoot` with `--Scanner:AzureDevOps:OrganizationUrl https://dev.azure.com/example` and `--Scanner:AzureDevOps:Pat your-test-pat-0001`. `Main_WithSeveralSettingsMissing_ExitsTwoListingEachInOneLine` clears and restores `Scanner__AzureDevOps__Pat` instead of `Scanner__Source__LocalRoot`, and expects `Scanner:AzureDevOps:Pat` in place of `Scanner:Source:LocalRoot`. `CreateBuilder_InQueueMode_RegistersTheLoopTheQueueAndTheLocalFolders` becomes `..._RegistersTheLoopTheQueueAndTheClone`, expecting `AzureDevOpsCloneSource`. Add:

```csharp
// Decision 13: exit 1 like any run that cannot go on, with one line and no stack trace
[Test]
public async Task RunAsync_WhenAzureDevOpsRefusesTheToken_ExitsOneWithOneLine()
// An in-memory SQLite database (SqliteTestDatabase.ReplaceDatabaseIn), a FakeScanQueue with one valid message, and a
// FakeRepositorySource whose Failure is an AzureDevOpsAuthenticationException. Exit code 1; output contains
// "Azure DevOps refused the token, scanner stopped:"; exactly one "FTL]" line; no "   at ".
```

`AzureDevOpsOptionsTests`: add `CreateBuilder_InDevelopment_ClonesFromBlacklightInternalsMo`: `--environment Development`, no organisation argument, gives `OrganizationUrl` `https://dev.azure.com/BlacklightInternalsMo`.

`QueueModeTests`: in a `[OneTimeSetUp]`, `BareRepository.Create(<repos>, "Lumina", "secrets-repo", "develop", FixtureSecrets.FolderPath)`, keeping the returned commit ID in a field for Task 6. `RunAsync` passes `--Scanner:AzureDevOps:OrganizationUrl <BareRepository.OrganizationUrl(repos)>` and `--Scanner:AzureDevOps:Pat your-test-pat-0001` in place of `LocalRoot`, and registers `new WorkspaceFolders(<a TempFolder>, NullLogger<WorkspaceFolders>.Instance)`, so clones never land in the real `%TEMP%\reposcanner`. Update the class and `RunAsync` comments. The five Issue 7 tests are otherwise unchanged and must still pass, now through a real clone.

- [ ] **Step 6: Run them to see them fail**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~ScanOrchestratorTests|FullyQualifiedName~ProgramTests|FullyQualifiedName~QueueModeTests|FullyQualifiedName~DatabaseRegistrationTests|FullyQualifiedName~AzureDevOpsOptionsTests" -p:CollectCoverage=false`
Expected: the new tests fail (the loop logs and swallows the exception; Program registers `LocalFolderSource`).

- [ ] **Step 7: Change the loop and the program, and remove the Issue 7 source**

- `ScanOrchestrator.HandleAsync`: a `catch (AzureDevOpsAuthenticationException)` placed before the general catch increments `counts.Failed` and rethrows, so the general catch never logs and swallows it. `RunAsync` catches `AzureDevOpsAuthenticationException`, calls `LogStopped("Azure DevOps refused the token", counts)`, and rethrows.
- `Program.RunAsync`: a catch before the general one logs `Azure DevOps refused the token, scanner stopped: {Problem}` with `Log.Fatal` and no exception, and returns `ExitCodes.Failure`.
- `Program.CreateBuilder`, queue mode: `AzureDevOpsOptions` bound from its section, `ValidateDataAnnotations().ValidateOnStart()`; `GitProcess` and `new WorkspaceFolders(Path.Combine(Path.GetTempPath(), "reposcanner"), ...)` as singletons; `AzureDevOpsCloneSource` as the `IRepositorySource`. `SourceOptions` and `LocalFolderSource` are no longer registered; delete them and their tests.
- `appsettings.Development.json`: `"AzureDevOps": { "OrganizationUrl": "https://dev.azure.com/BlacklightInternalsMo" }` inside `"Scanner"`.
- Comments: `Program.cs:109` names the PAT instead of `LocalRoot`; `ScanRequestReader.cs:163` says the name becomes part of the clone URL.

- [ ] **Step 8: Amend ADR-016 and ADR-017**

Add the two amendments word for word from the spec's "ADR amendments" section: ADR-016 gets a new `## Amendments` heading, ADR-017's goes under its existing one. In `docs/adr/README.md`, ADR-016 becomes `Accepted, amended 2026-10-01` and ADR-017 `Accepted, amended 2026-09-24 and 2026-10-01`.

- [ ] **Step 9: Run the tests to see them pass, then check nothing still uses LocalRoot**

Run the step 6 command, then `dotnet build RepoScanner.sln` (zero warnings) and `dotnet test RepoScanner.sln`. Expected: all pass, coverage gate met.
Run: `git grep -n "LocalRoot\|LocalFolderSource\|SourceOptions" -- src tests`
Expected: no output. (`README.md` and `scripts/enqueue-local.ps1` change in Task 6.)

- [ ] **Step 10: Stop for review, then commit and set #37 to Done**

```bash
git add -A src tests docs/adr
git commit -m "This commit adds AzureDevOpsCloneSource, which clones the message's branch into a new WorkspaceFolders folder, logs free disk space before and after, deletes the folder if anything fails, and turns git's rejected-token errors into AzureDevOpsAuthenticationException, which stops the queue loop and exits 1 with the message left; registers it with AzureDevOpsOptions in place of LocalFolderSource and SourceOptions, which are removed with their tests; points Development at BlacklightInternalsMo; clones a bare copy of the secrets fixture in the queue-mode tests; and amends ADR-016 and ADR-017 #37"
```

- [ ] **Step 11: The LocalRoot reminder**

Remind the user that their `Scanner__Source__LocalRoot` user environment variable is now unused (task #37's description has the same reminder). With their go-ahead, run:

```powershell
[Environment]::SetEnvironmentVariable("Scanner__Source__LocalRoot", $null, "User")
```

Check: `[Environment]::GetEnvironmentVariable("Scanner__Source__LocalRoot", "User")` prints nothing.

---

### Task 4: The commit ID on the scan run (#38)

**Files:**
- Modify: `src/RepoScanner.Scanner/Sources/RepositoryWorkspace.cs`, `src/RepoScanner.Scanner/Git/GitCommand.cs`, `src/RepoScanner.Scanner/Sources/AzureDevOpsCloneSource.cs`, `src/RepoScanner.Scanner/RepositoryScanRunner.cs`
- Test: `tests/RepoScanner.Scanner.Tests/Git/GitCommandTests.cs`, `tests/RepoScanner.Scanner.Tests/Sources/AzureDevOpsCloneSourceTests.cs`, `tests/RepoScanner.Scanner.Tests/RepositoryScanRunnerTests.cs`

**Interfaces:**
- Consumes: `AzureDevOpsCloneSource`, `BareRepository.Create`'s returned commit ID (Task 3).
- Produces: `public RepositoryWorkspace(string folder, string? commitSha, Func<ValueTask> cleanUp)`; `public string? CommitSha { get; }` (`ForExistingFolder` gives null); `public static GitCommand GitCommand.RevParseHead(string folder)`; `internal static string AzureDevOpsCloneSource.ParseCommitId(string output)`.

- [ ] **Step 1: Set #38 to Doing**

- [ ] **Step 2: Write the failing tests**

```csharp
// GitCommandTests
[Test] public void RevParseHead_HasTheArgumentsOfTheDesign()
// Name "rev-parse"; Arguments { "-C", Folder, "rev-parse", "HEAD" }; Secrets empty; prompts off

// AzureDevOpsCloneSourceTests
[Test] public async Task OpenAsync_GivesTheCommitIdOfTheBranchHead()
// workspace.CommitSha equals the ID BareRepository.Create returned

[Test] public async Task OpenAsync_LogsTheCommitId()
// The "Cloned ..." record's Message contains " at " + the commit ID

[Test] public void ParseCommitId_GivesTheTrimmedId()
// ParseCommitId("27487b917ee49183c0c677159a43707cf20a9219\n") is "27487b917ee49183c0c677159a43707cf20a9219"

[TestCase("")]
[TestCase("fatal: not a git repository")]
// SHA-256, which does not fit varchar(40)
[TestCase("6f1ed002ab5595859014ebf0951522d9e1b1f6b3c3f7a4c3c1f1e0c8f1b2a3d4")]
public void ParseCommitId_ForAnythingButFortyHexCharacters_Throws(string output)
// InvalidOperationException with Message "git rev-parse HEAD gave an unexpected commit ID"

// RepositoryScanRunnerTests, with CommitId = "27487b917ee49183c0c677159a43707cf20a9219" and
// OpenClone() returning new RepositoryWorkspace(Folder, CommitId, () => ValueTask.CompletedTask)
[Test] public async Task RunAsync_SavesTheWorkspacesCommitId()
// Completed run; CommitSha is CommitId
[Test] public async Task RunAsync_WhenSomeScannersFail_StillSavesTheCommitId()
// PartiallyCompleted; CommitSha is CommitId
[Test] public async Task RunAsync_WhenEveryScannerFails_SavesTheCommitId()
// Failed; CommitSha is CommitId
[Test] public async Task RunAsync_WhenTheTimeLimitIsReached_SavesTheCommitId()
// Failed with the time-limit message; CommitSha is CommitId
```

Extend `RunAsync_WhenTheWorkspaceCannotBeOpened_MarksTheRunFailedWithTheReasonAndRethrows` with `Assert.That(run.CommitSha, Is.Null)`. `WorkspaceThatCounts` passes `commitSha: null`. `RunAsync_CompletesTheRunWithTheNewCountAndOneScannerResult` keeps its `CommitSha` null check, which is now the `--path` case.

- [ ] **Step 3: Run them to see them fail**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~GitCommandTests|FullyQualifiedName~AzureDevOpsCloneSourceTests|FullyQualifiedName~RepositoryScanRunnerTests" -p:CollectCoverage=false`
Expected: build fails (the new constructor and methods do not exist).

- [ ] **Step 4: Implement**

- `RepositoryWorkspace`: the new constructor and property; `ForExistingFolder` passes null.
- `GitCommand.RevParseHead`: the arguments above, the prompts-off environment, no secrets.
- `AzureDevOpsCloneSource.OpenAsync`: after the clone, `ParseCommitId(await _git.RunAsync(GitCommand.RevParseHead(folder), ...))`; the log line becomes the spec's, with `at {CommitSha}`; the workspace gets the commit ID. `ParseCommitId` trims and accepts exactly 40 characters of `0-9a-f`.
- `RepositoryScanRunner.RunAsync`: `string? commitSha = null;` before the `try`, set from `workspace.CommitSha` once it is open, passed in place of `CommitSha: null` to both completions in the scan, and to `RecordFailureAsync`, which gains a `string? commitSha` parameter. Both catch blocks pass it.

- [ ] **Step 5: Run the tests to see them pass, then the full build and tests**

Run the step 3 command, then `dotnet build RepoScanner.sln` and `dotnet test RepoScanner.sln`. Expected: all pass, coverage gate met.

- [ ] **Step 6: Stop for review, then commit and set #38 to Done**

```bash
git add src tests
git commit -m "This commit reads the clone's commit ID with git rev-parse HEAD, refusing anything but 40 hexadecimal characters, logs it with the clone, carries it on RepositoryWorkspace, and has RepositoryScanRunner save it on every completion after the clone: Completed, PartiallyCompleted and Failed; it stays empty when the clone fails and for --path #38"
```

---

### Task 5: The clone time limit (#40)

**Files:**
- Modify: `src/RepoScanner.Scanner/Configuration/AzureDevOpsOptions.cs`, `src/RepoScanner.Scanner/Git/GitProcess.cs`, `src/RepoScanner.Scanner/Sources/AzureDevOpsCloneSource.cs`
- Test: `tests/RepoScanner.Scanner.Tests/Configuration/AzureDevOpsOptionsTests.cs`, `tests/RepoScanner.Scanner.Tests/Git/GitProcessTests.cs`, `tests/RepoScanner.Scanner.Tests/Sources/AzureDevOpsCloneSourceTests.cs`

**Interfaces:**
- Consumes: `FakeAzureDevOps.StartStalling()` (Task 3).
- Produces: `[Range(1, 1440)] public int CloneTimeoutMinutes { get; init; } = 8;`; `public GitProcess(TimeProvider timeProvider, ILogger<GitProcess> logger)`; `public Task<string> RunAsync(GitCommand command, TimeSpan timeLimit, CancellationToken cancellationToken)`, which throws `TimeoutException` when `timeLimit` passes.

- [ ] **Step 1: Set #40 to Doing**

- [ ] **Step 2: Write the failing tests**

```csharp
// AzureDevOpsOptionsTests
[Test] public void CloneTimeoutMinutes_DefaultsToEight()
[TestCase(0)] [TestCase(1441)] public void Validate_CloneTimeoutOutsideOneTo1440_Fails(int minutes)
[TestCase(1)] [TestCase(1440)] public void Validate_CloneTimeoutInsideOneTo1440_Succeeds(int minutes)

// GitProcessTests (Git() now passes TimeProvider.System)
[Test] public void RunAsync_PastTheTimeLimit_StopsGitAndItsChildAndThrowsATimeout()
// alias "!sleep 60", timeLimit 1 second: Throws.TypeOf<TimeoutException>, in less than 15 seconds

// AzureDevOpsCloneSourceTests (Source() now builds GitProcess with a FakeTimeProvider _time)
[Test]
public async Task OpenAsync_WhenTheCloneTakesLongerThanTheLimit_SaysSoAndLeavesNoFolder()
{
    using (FakeAzureDevOps server = FakeAzureDevOps.StartStalling())
    {
        Task<RepositoryWorkspace> opening = Source(server.OrganizationUrl).OpenAsync(Request("Kudos Wall", "victim-repo", "main"), CancellationToken.None);
        await server.FirstRequest.WaitAsync(TimeSpan.FromSeconds(30));

        _time.Advance(TimeSpan.FromMinutes(8));

        TimeoutException exception = Assert.ThrowsAsync<TimeoutException>(() => opening);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Is.EqualTo("The clone took longer than 8 minutes and was stopped (Scanner:AzureDevOps:CloneTimeoutMinutes)"));
            Assert.That(Directory.EnumerateFileSystemEntries(_folders.Root), Is.Empty);
        }
    }
}
```

Every other `GitProcessTests` and `AzureDevOpsCloneSourceTests` test builds through `Git()` and `Source()`, so only those helpers change.

- [ ] **Step 3: Run them to see them fail**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~AzureDevOpsOptionsTests|FullyQualifiedName~GitProcessTests|FullyQualifiedName~AzureDevOpsCloneSourceTests" -p:CollectCoverage=false`
Expected: build fails (no `CloneTimeoutMinutes`, no `timeLimit` parameter).

- [ ] **Step 4: Implement**

- `GitProcess.RunAsync`: a `CancellationTokenSource(timeLimit, _timeProvider)` linked with the caller's token. When the limit fires and the caller's token has not, git and its child processes are stopped as for cancellation, and it throws `TimeoutException("git " + command.Name + " took longer than its time limit of " + timeLimit + " and was stopped")`.
- `AzureDevOpsCloneSource`: the clone gets `TimeSpan.FromMinutes(CloneTimeoutMinutes)`, `rev-parse` 1 minute. A `TimeoutException` from the clone is rethrown with the spec's text (minutes from the setting), the original inner, after the folder is deleted.
- `Program` needs no change: `TimeProvider` is already registered.

- [ ] **Step 5: Run the tests to see them pass, then the full build and tests**

Run the step 3 command, then `dotnet build RepoScanner.sln` and `dotnet test RepoScanner.sln`. Expected: all pass, coverage gate met.

- [ ] **Step 6: Stop for review, then commit and set #40 to Done**

```bash
git add src tests
git commit -m "This commit adds Scanner:AzureDevOps:CloneTimeoutMinutes, 8 by default and 1 to 1440, ADR-016's cap: GitProcess stops git and its child processes when a command's time limit passes, and the clone source then deletes the folder and fails the scan run with The clone took longer than 8 minutes and was stopped, leaving the message to be retried #40"
```

---

### Task 6: Acceptance tests, the script, the README and the manual checks (#41)

**Files:**
- Modify: `tests/RepoScanner.Scanner.Tests/QueueModeTests.cs`, `scripts/enqueue-local.ps1`, `README.md`

**Interfaces:**
- Consumes: the `QueueModeTests` set-up from Task 3 (bare copy of the secrets fixture, its commit ID, the test `WorkspaceFolders`).

- [ ] **Step 1: Set #41 to Doing**

- [ ] **Step 2: Write the Issue 8 acceptance tests**

One per success criterion, through `Program`:

```csharp
// Issue 8 criterion 1: a queue message for a repository produces findings from the cloned source, with its commit ID
[Test] public async Task Issue8_AMessage_ClonesTheRepositoryAndSavesItsFindingsAndCommitId()
// Exit 0; one Completed run whose CommitSha is the bare repository's commit ID; findings equal FixtureSecrets.All

// Issue 8 criterion 2, with a failed clone too, since error messages are where a token would most likely leak
[Test] public async Task Issue8_NothingTheProgramPrints_ContainsThePat()
// Messages for secrets-repo and ghost. leaked = output contains Pat or Base64; Is.False

// Issue 8 criterion 3
[Test] public async Task Issue8_TheWorkspaceRoot_IsEmptyAfterACompletedAndAFailedScan()
// Messages for secrets-repo and ghost: statuses Completed and Failed; the test WorkspaceFolders root has no entries
```

- [ ] **Step 3: Run them**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~QueueModeTests" -p:CollectCoverage=false`
Expected: all pass. These pin behaviour Tasks 1 to 5 built; if one fails, fix the code in the task it belongs to, not the test.

- [ ] **Step 4: Update `scripts/enqueue-local.ps1`**

`-ProjectName` defaults to `'Kudos Wall'` and `-DefaultBranch` to `'main'`. The help says the scanner clones the repository from `Scanner:AzureDevOps:OrganizationUrl` (Issue 8), with nothing about `LocalRoot`; `.PARAMETER RepositoryName` is "The repository to clone, such as victim-repo".

- [ ] **Step 5: Update `README.md`**

- **Prerequisites:** "Git 2.31 or later on PATH: queue mode runs it to clone, and gives it the PAT through `GIT_CONFIG_COUNT`".
- **Configuration:** rows for `Scanner:AzureDevOps:OrganizationUrl`, `Scanner:AzureDevOps:Pat` and `Scanner:AzureDevOps:CloneTimeoutMinutes` as in the spec's Settings table; the `Scanner:Source:LocalRoot` row and the "added with cloning, in Issue 8" line removed; "only the database setting is needed" with `--path` also covers the Azure DevOps settings.
- **Scan from the queue locally:** the opening says the code is cloned from Azure DevOps. One-off setup steps 2 and 3 become the spec's Local setup steps 1 to 3 (creating the PAT, `setx Scanner__AzureDevOps__Pat`, a new terminal). The Storage Explorer message uses `"projectName": "Kudos Wall"`; the field table says `repositoryName` is the repository to clone. **Run it** gains: a missing repository fails with git's reason and is retried; a rejected PAT stops the run with exit 1 and `Azure DevOps refused the token`, leaving the message; the clone goes in `%TEMP%\reposcanner` and is deleted afterwards.
- **Known limitations:** a "Cloning" group with the spec's limitations; the Issue 7 line about local folders removed.
- **Exit codes:** 1 also covers a rejected PAT.

Then run `git grep -n "LocalRoot" -- README.md scripts`. Expected: no output.

- [ ] **Step 6: Full build and tests**

`dotnet build RepoScanner.sln` (zero warnings), then `dotnet test RepoScanner.sln`. Expected: all pass, Core and Scanner at 80% or more.

- [ ] **Step 7: Manual checks with the user**

The user creates the PAT and runs `setx Scanner__AzureDevOps__Pat` themselves (Local setup steps 1 to 3); never ask for its value. With Azurite running:

1. `.\scripts\enqueue-local.ps1 -RepositoryName victim-repo`, then `dotnet run --project src/RepoScanner.Scanner`. Expected: the log shows `Cloned Kudos Wall/victim-repo main at b653342...`, the run stops with `1 scanned`; in SSMS the scan run is Completed with `CommitSha` starting `b653342` and findings saved; `%TEMP%\reposcanner` is empty.
2. In one terminal, `$env:Scanner__AzureDevOps__Pat = "your-wrong-pat"`, add a message, and run. Expected: exit 1, `Azure DevOps refused the token, scanner stopped: ...`, the scan run Failed with that reason, the message still on `scan-nightly`, no sign-in window.
3. `.\scripts\enqueue-local.ps1 -RepositoryName ghost`, then run. Expected: a Failed scan run with git's "does not exist" reason, and the message left to be retried.

Note the results for the commit message and the pull request.

- [ ] **Step 8: Stop for review, then commit and set #41 to Done**

```bash
git add tests/RepoScanner.Scanner.Tests/QueueModeTests.cs scripts/enqueue-local.ps1 README.md
git commit -m "This commit adds Issue 8's acceptance tests through the whole program, cloning a bare copy of the secrets fixture: its findings and commit ID are saved, nothing printed contains the PAT, and the workspace root is empty after a completed and a failed scan; sets enqueue-local.ps1's defaults to Kudos Wall and main; and documents cloning in the README: Git 2.31, the Scanner:AzureDevOps settings, setting up the PAT locally, a rejected PAT and the known limitations #41"
```

Then tell the user Issue 8's tasks are done and ask whether to push and open the pull request.
