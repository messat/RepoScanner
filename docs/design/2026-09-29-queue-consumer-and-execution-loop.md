# Issue 7: Queue consumer and execution loop (design)

- **Status:** Draft, for review
- **Date:** 2026-09-29
- **Work item:** Issue 7, #28 (tasks #29 to #35)
- **Branch:** `feature/28-queue-consumer-and-execution-loop`

## Purpose

Until now the scanner only scans a folder named with `--path`. Without `--path` it logs "No work to do" and exits. This Issue makes it the job ADR-001 describes: it takes scan requests off a storage queue one at a time, scans each repository, saves the findings, deletes the message, and exits when the queue is empty or its time budget is spent.

Two Container Apps Jobs run the same image (ADR-009, ADR-011). The nightly job reads `scan-nightly`, which the enqueuer fills with one message per repository at 02:00. The on-demand job reads `scan-ondemand`, which the dashboard's "Scan now" button fills with one message. Only `Scanner:Queue:Name` differs between them. A message that keeps failing is moved to its lane's poison queue, `scan-nightly-poison` or `scan-ondemand-poison` (ADR-013).

Cloning is Issue 8. In this Issue, a message is scanned from a local folder named after the repository, so the whole loop can be built, tested and shown on a developer's machine with Azurite and no Azure DevOps. `--path` stays: it scans a folder with no queue and no Azure DevOps, which remains useful once queue mode clones for real.

## Success criteria

From Issue 7 (#28):

1. One local message produces one ScanRun and findings.
2. An empty queue exits 0 within seconds.
3. A malformed message lands in its lane's poison queue (`scan-nightly-poison` or `scan-ondemand-poison`).
4. A message for a repository with a live Running ScanRun is deleted and logged as skipped.
5. A Running ScanRun older than `ScanTimeoutPerRepoMinutes` + 5 minutes is marked Failed, and the repository is scanned.

## Decisions

Agreed in the design discussion on 2026-09-29.

| # | Question | Decision | Reason |
|---|---|---|---|
| 1 | Who defines the message format | This document. The enqueuer and the dashboard follow it | Nothing exists yet, and the scanner is the reader |
| 2 | What the message carries | The repository's Azure DevOps ID, project name, repository name and default branch, with a schema version and a correlation ID | The scanner can build the clone URL (Issue 8) without calling the Azure DevOps REST API. The dashboard has these fields in its `Repositories` row, and the enqueuer gets them from its REST call. `StartRunAsync` already takes them |
| 3 | How it is encoded | Plain UTF-8 JSON, not Base64. Field names are matched ignoring case; extra fields are ignored | Readable in Storage Explorer and in logs, and `QueueClient`'s default. A sender can add an optional field without breaking the scanner |
| 4 | When `schemaVersion` changes | Only when the format changes in a way an older scanner cannot read safely, such as a new required field. The scanner reads version `1` only | A retry never changes the message: Azure counts attempts in `DequeueCount`, beside the message |
| 5 | Nightly or on-demand | From the queue the scanner reads: `scan-nightly` is `Nightly`, `scan-ondemand` is `OnDemand`. `Scanner:Queue:Name` must be one of the two | The queue already says it, so the message does not repeat it (ADR-011: no queue-selection code) |
| 6 | Where the code comes from in Issue 7 | `Scanner:Source:LocalRoot` plus `repositoryName`, behind `IRepositorySource`. Issue 8 adds `git clone` behind the same interface | The loop is the real code from the first day; Issue 8 changes only where the folder comes from. Tests need no network |
| 7 | `--path` | Kept. Its "scan and save" code moves into `RepositoryScanRunner`, which queue mode uses too | One copy of the scan, so the two modes cannot drift apart |
| 8 | When the folder is opened | After the ScanRun is started, inside the runner, and always closed afterwards | A missing folder, and from Issue 8 a failed clone, is recorded as a Failed ScanRun (ADR-013). Issue 8 deletes its clone on close, even on failure |
| 9 | Time limits | Run budget 50 minutes, 15 minutes per repository, messages hidden for 65 minutes; 1 minute in `Development` | See Settings |
| 10 | How many messages are taken at once | One | A batch would hide every message in it for 65 minutes while one run works through them, so a second run (`maxExecutions` above 1, ADR-012) could not help |
| 11 | When a message is deleted | After its ScanRun is saved as Completed or PartiallyCompleted (ADR-013) | A crash between saving and deleting scans the repository twice, which is harmless: findings are matched on fingerprint (ADR-004) |
| 12 | A failed scan | The message is left. It reappears after the visibility timeout and is tried again | Retrying helps with things that change, such as a database blip |
| 13 | When a message is poisoned | On its 4th pick-up (`DequeueCount` above `MaxDequeueCount`, 3), and on the first read of a malformed one | ADR-013. Retrying a malformed message cannot help |
| 14 | How it is poisoned | The body is copied unchanged to the lane's poison queue, kept for 30 days, then the original is deleted. One "message poisoned" log line | Copying first means it can never be lost. An unchanged body can be moved back with Storage Explorer's **Move**. Azure's default expiry is 7 days, too short to be noticed |
| 15 | A malformed message's ScanRun | None | Its repository details cannot be trusted. It is the one exception to ADR-013's "a failed scan is always visible in ScanRun", recorded in the ADR-013 amendment |
| 16 | A repository with a Running ScanRun | Started `ScanTimeoutPerRepoMinutes` + 5 minutes ago or less: the message is deleted and logged as skipped. Longer ago: that run is marked Failed as abandoned, and the repository is scanned | A live scan is stopped at 15 minutes, so a Running row older than 20 minutes must belong to a run that died. New ADR-027 (#33) |
| 17 | Exit codes in queue mode | 0 when the queue was worked through or the budget ran out, even if some repositories failed. 1 when the queue or the database cannot be reached, or the run is stopped. 2 when a queue is missing or a setting is wrong | A failed repository is recorded in its ScanRun and retried; it is not a failed execution |
| 18 | A missing queue | Found on first use and reported as a settings error, exit 2. The app never creates queues | In Azure the scanner may only use named queues (ADR-014), and may only add messages to the poison queue, so it cannot check that one at start-up. Locally, `enqueue-local.ps1` creates them |
| 19 | Signing in to Azure | Built now: `Scanner:Queue:ServiceUri` with `DefaultAzureCredential` (Azure.Identity) | Moving to Azure is then a settings change, not a code change (ADR-007, ADR-017) |
| 20 | Azurite | Stays on the developer's machine, started with npm. Docker Compose moves to Issue 9, where the scanner container reaches Azurite through `host.docker.internal` | In Azure the storage account sits outside the Container Apps environment, as SQL Server does; the local setup mirrors that |
| 21 | How the tests reach a queue | Through `IScanQueue`, faked in memory for the loop, and a hand-written fake `QueueClient` for `QueueConsumer`. Time comes from `TimeProvider`, faked with `FakeTimeProvider` | `dotnet test` still needs no Azurite and no SQL Server. The repository writes its fakes by hand; `FakeTimeProvider` is from the same Microsoft family as the `FakeLogger` already used |
| 22 | New packages | `Azure.Storage.Queues` **12.27.1** and `Azure.Identity` **1.21.0** in `RepoScanner.Scanner`; `Microsoft.Extensions.TimeProvider.Testing` **10.10.0** in the Scanner tests. The latest stable versions on 2026-09-29 | Core stays free of packages |
| 23 | New ADRs | ADR-027, skip and take-over (#33). ADR-013 is amended (#30) | See those tasks |
| 24 | Where this document lives | `docs/design/` | Same as Issues 2 to 6 |

## Project layout

```
src/RepoScanner.Core/
  Persistence/     IScanRunRepository: + GetRunningAsync                              #33
src/RepoScanner.Data/
  Repositories/    ScanRunRepository: + GetRunningAsync                               #33
src/RepoScanner.Scanner/                 + Azure.Storage.Queues, Azure.Identity
  Queue/           ScanRequest, ScanRequestReader                                     #29
                   IScanQueue, ScanQueueMessage, QueueConsumer, QueueClients,
                   ScanQueueNotFoundException                                         #30
  Sources/         IRepositorySource, RepositoryWorkspace, LocalFolderSource          #31
  Configuration/   QueueOptions: Name rule, Trigger, PoisonMessageTimeToLiveDays      #30
                   ExecutionOptions, SourceOptions                                    #31
  Local/           LocalScanRunner, now calling RepositoryScanRunner                  #31
  RepositoryScanRunner.cs   scan one repository and save it, moved from LocalScanRunner  #31
  ScanOrchestrator.cs       the loop                                                  #31, #33
  Program.cs                registrations; a missing queue exits 2                    #30, #31
  appsettings.Development.json   VisibilityTimeoutMinutes 1                           #31

scripts/enqueue-local.ps1                creates missing queues, adds a message       #34

tests/RepoScanner.Scanner.Tests/
  Queue/           reader, consumer and fake queue tests, FakeQueueClient, FakeScanQueue
  Sources/         LocalFolderSource tests
  RepositoryScanRunnerTests.cs           the scan tests, moved from LocalScanRunnerTests
  ScanOrchestratorTests.cs               the loop
  QueueModeTests.cs                      the acceptance criteria through Program     #35
tests/RepoScanner.Data.Tests/            GetRunningAsync                             #33

docs/adr/ADR-013-delete-after-upsert-poison-queue-after-three-failures.md   amended  #30
docs/adr/ADR-027-skip-a-repository-being-scanned-take-over-an-abandoned-run.md  new  #33
```

Classes are `sealed` or `static` and follow ADR-021, as in Issues 2 to 6. `QueueConsumer`, `LocalFolderSource` and `ScanOrchestrator` are singletons. `RepositoryScanRunner` is scoped, like the repositories it uses, and the loop makes a new scope for each message, so each repository gets a fresh `DbContext`.

## Message format (#29)

```json
{
  "schemaVersion": 1,
  "correlationId": "5b1c6f3e-8a2d-4e7f-9c1a-2d3e4f5a6b7c",
  "repositoryId": "0f8fad5b-d9cb-469f-a165-70867728950e",
  "projectName": "Lumina",
  "repositoryName": "RepoScanner",
  "defaultBranch": "develop"
}
```

| Field | Rule | Used for |
|---|---|---|
| `schemaVersion` | The number `1` | Refusing a format this scanner cannot read (decision 4) |
| `correlationId` | A GUID, not all zeros | `ScanRun.CorrelationId`, and every log line about the message |
| `repositoryId` | A GUID, not all zeros | `Repositories.AzureDevOpsId`, the key that survives a rename |
| `projectName` | Not empty | The clone URL (Issue 8); `Repositories.ProjectName` |
| `repositoryName` | Not empty; not `.` or `..`; no `\`, `/` or other character a file name cannot hold | The clone URL (Issue 8), and the folder under `LocalRoot` in Issue 7; `Repositories.Name` |
| `defaultBranch` | Not empty; not starting with `refs/` | The branch to clone (Issue 8); `Repositories.DefaultBranch`. The REST API gives `refs/heads/develop`; the sender strips the prefix, because `git clone --branch` takes `develop` |

```csharp
namespace RepoScanner.Scanner.Queue
{
    // One scan request from scan-nightly or scan-ondemand (Issue 7 design, Message format)
    public sealed record ScanRequest(Guid CorrelationId, Guid RepositoryId, string ProjectName, string RepositoryName, string DefaultBranch)
    {
        public const int SchemaVersion = 1;

        public RepositoryDetails ToRepositoryDetails();
    }

    public static class ScanRequestReader
    {
        // False, with what is wrong, when the body is not a scan request this scanner can read
        public static bool TryRead(string body, [NotNullWhen(true)] out ScanRequest? request, [NotNullWhen(false)] out string? problem);
    }
}
```

The body is read with `System.Text.Json`, matching field names ignoring case. A body that is not JSON, including one sent as Base64, a missing or empty field, a field sent twice (in any case), a wrong type, an unknown `schemaVersion`, or a value breaking a rule above is malformed. The characters `repositoryName` cannot hold are Windows' list, `< > : " | ? *` and control characters, written out in the code, because .NET's own list on Linux, where the container runs, allows them. `problem` names the first thing wrong, such as `repositoryName must not contain \ or /`, and is the reason in the "message poisoned" log line. It never includes the body.

`repositoryName` is untrusted input: without the rule, `..\..\Windows` would point the scan at another folder. `LocalFolderSource` checks again that the folder it opens is directly inside `LocalRoot`.

## Queue access (#30)

```csharp
namespace RepoScanner.Scanner.Queue
{
    public sealed record ScanQueueMessage(string MessageId, string PopReceipt, string Body, long DequeueCount);

    // The lane's queue and its poison queue. Faked in memory in the loop's tests.
    public interface IScanQueue
    {
        // One message, hidden from other runs for visibilityTimeout; null when the queue is empty
        Task<ScanQueueMessage?> ReceiveAsync(TimeSpan visibilityTimeout, CancellationToken cancellationToken);

        Task DeleteAsync(ScanQueueMessage message, CancellationToken cancellationToken);

        // Copies the body unchanged to the poison queue, kept for PoisonMessageTimeToLiveDays, then deletes the original
        Task MoveToPoisonAsync(ScanQueueMessage message, CancellationToken cancellationToken);
    }

    // IScanQueue over two Azure QueueClients
    public sealed class QueueConsumer : IScanQueue
    {
        public QueueConsumer(QueueClient queue, QueueClient poisonQueue, IOptions<QueueOptions> options, ILogger<QueueConsumer> logger);
    }
}
```

- `QueueClients.Create(QueueOptions)` builds both clients: from `ConnectionString` locally, or from `ServiceUri` with `DefaultAzureCredential` in Azure. In Azure, `AZURE_CLIENT_ID` picks the user-assigned identity `id-scanner` (ADR-014); that is set in the deployment Issues.
- `ReceiveAsync` asks for one message. `DeleteAsync` uses the message ID and pop receipt. A delete that finds the message gone (Azure's `MessageNotFound`) is logged as a warning, not a failure: another run took it after it reappeared.
- Azure's `QueueNotFound`, from either queue, becomes `ScanQueueNotFoundException`, naming the queue. `Program` logs it as one line and exits 2, as for any settings problem. If the poison queue is the missing one, the original message is not deleted, because the copy comes first.
- Any other queue error stops the run with exit 1. The message being worked on reappears after its visibility timeout.

**ADR-013 amendment, added in #30.** *2026-09-29 (Issue 7): each lane has its own poison queue, as decided. A malformed message, including one with an unknown `schemaVersion`, is moved to it on its first read and gets no ScanRun, since its repository details cannot be trusted. The poison copy keeps the body unchanged and expires after 30 days. In `Development` the visibility timeout is 1 minute, so a retry can be watched locally; in Azure it stays at least replicaTimeout plus a buffer. The decision is otherwise unchanged.*

## Scanning one repository (#31)

`LocalScanRunner`'s scan, reconcile and save code moves unchanged into `RepositoryScanRunner`. What changes is where the repository details and the folder come from.

```csharp
namespace RepoScanner.Scanner
{
    public sealed class RepositoryScanRunner
    {
        public RepositoryScanRunner(IScanRunRepository scanRuns, IFindingRepository findings, IEnumerable<IScanner> scanners,
            TimeProvider timeProvider, ILogger<RepositoryScanRunner> logger);

        // Starts a Running ScanRun, opens the folder, runs every scanner, reconciles, and completes the run.
        // A failure after the run started, including opening the folder or going over timeLimit, marks the
        // run Failed and is rethrown, as today.
        public Task RunAsync(RepositoryDetails repository, ScanTrigger trigger, Guid correlationId,
            Func<CancellationToken, Task<RepositoryWorkspace>> openWorkspace, TimeSpan? timeLimit, CancellationToken cancellationToken);
    }
}

namespace RepoScanner.Scanner.Sources
{
    // A folder holding a repository's code for one scan. Closing it cleans up: nothing for a local folder,
    // deleting the clone from Issue 8.
    public sealed class RepositoryWorkspace : IAsyncDisposable
    {
        public string Folder { get; }

        public static RepositoryWorkspace ForExistingFolder(string folder);
    }

    public interface IRepositorySource
    {
        Task<RepositoryWorkspace> OpenAsync(ScanRequest request, CancellationToken cancellationToken);
    }

    // Issue 7 only: the folder named repositoryName directly inside Scanner:Source:LocalRoot
    public sealed class LocalFolderSource : IRepositorySource
    {
        public LocalFolderSource(IOptions<SourceOptions> options);
    }
}
```

| Mode | Repository details | Folder | Trigger | Time limit |
|---|---|---|---|---|
| `--path C:\folder` | `DescribeFolder`: project `Local`, an ID made from the path (unchanged) | The folder given | `OnDemand` (unchanged) | None (unchanged) |
| Queue | `request.ToRepositoryDetails()` | `LocalRoot\<repositoryName>` | From the queue name | `ScanTimeoutPerRepoMinutes` |

- A folder that does not exist fails with `No folder named Ghost in <LocalRoot>`. The ScanRun is marked Failed with that message, and the message is retried, then poisoned.
- When the time limit is hit, the scan is cancelled and the ScanRun's `ErrorMessage` is `The scan took longer than 15 minutes and was stopped (Scanner:Execution:ScanTimeoutPerRepoMinutes)`. The runner owns the timer, so it can tell this apart from the job being stopped.
- Scanning the same folder with `--path` and through the queue gives the same findings under two `Repositories` rows, one in project `Local` and one with the message's ID. That is expected: `Local` rows are for development only.

## The loop (#31)

`ScanOrchestrator.RunAsync`, once per execution:

1. Log `Scanner starting in {EnvironmentName} for queue {QueueName}`, as today, and start the budget clock.
2. If less than `ScanTimeoutPerRepoMinutes` is left of `MaxRunDurationMinutes`, stop: a repository that started now might not finish before Azure stops the job.
3. Take one message, hidden for `VisibilityTimeoutMinutes`. None: stop.
4. Read it (#29). Malformed: poison it, then go back to 2.
5. Picked up more than `MaxDequeueCount` times: poison it (see Poison), then go back to 2.
6. Check for a Running ScanRun (#33). Skipped: go back to 2.
7. Scan it with `RepositoryScanRunner`, the folder from `IRepositorySource` and the per-repository time limit.
8. Saved: delete the message. Failed: log a warning with the repository, message ID and attempt, and leave the message.
9. Go back to 2.

When it stops, it logs one line saying why (`queue empty`, `time budget spent` or `stopping`) with how many messages were scanned, failed, skipped and poisoned. The fuller run summary is Issue 10.

- Messages are worked on one after another; only one repository is scanned at a time in a run. With `maxExecutions` 1 (ADR-012) there is one run at a time.
- A database error in steps 5 or 6 stops the run with exit 1, leaving the message: every other message would fail the same way.
- When the job is asked to stop (Ctrl+C locally, SIGTERM in Azure), the current scan is cancelled and marked Failed, as `--path` does today. Its message is left, and the process exits 1.

**Worked example.** Five repositories at about 6 minutes each: A, B and D finish and are deleted; C hits 15 minutes at 02:29 and is left; at 02:36 only 14 minutes are left, so E is not started and the run exits 0. Azure starts another run for E. C reappears at 03:19 and fails twice more; on its 4th pick-up it is poisoned.

## Skip and stale runs (#33)

```csharp
// IScanRunRepository: the repository's Running scan runs, matched on AzureDevOpsId. Uses the (RepositoryId, Status) index.
Task<IReadOnlyList<ScanRun>> GetRunningAsync(Guid azureDevOpsId, CancellationToken cancellationToken);
```

The "stale after" age is `ScanTimeoutPerRepoMinutes` + 5 minutes: 20 in Azure and locally.

| Running rows for the repository | Action |
|---|---|
| None | Scan |
| At least one started 20 minutes ago or less | Delete the message; log `Skipped {Repository}: scan run {ScanRunId} is already scanning it`. No new ScanRun |
| Only rows started more than 20 minutes ago | Mark each Failed with `Abandoned: still Running more than 20 minutes after it started, so the run that started it had stopped`, then scan |

Rows are marked Failed with the existing `CompleteRunAsync`. The 5 minutes cover saving after a scan stops and small clock differences between machines.

**ADR-027, added in #33:** *Skip a repository that is being scanned; take over a run that has been Running longer than the per-repository timeout plus five minutes.* Context: the nightly and on-demand jobs share one image and can pick up the same repository at the same time (#28). Consequences: a skipped request is not kept, and two runs that check in the same instant can both scan, which is accepted because ADR-012 builds no locking and findings match on fingerprint.

## Poison

| Why | ScanRun |
|---|---|
| Malformed (step 4) | None (decision 15) |
| 4th pick-up (step 5) | Rows Running for more than 20 minutes, left by a crash, are marked Failed with `Moved to <poison queue> after 3 attempts`. A younger Running row belongs to another live run and is left alone. A failure that threw was already recorded with its error when it happened (ADR-013) |

Both log `Message {MessageId} moved to {PoisonQueueName}: {Reason}` with the repository when known, the correlation ID and `DequeueCount`, as a warning. That line is what the poison alert in Issue 10 watches, because Azure's queue message count metric covers the whole storage account and updates hourly.

**Retrying a poisoned message** needs no code: fix the cause, then click "Scan now" in the dashboard, or **Move** the message back in Storage Explorer (a moved message starts again at `DequeueCount` 1). A nightly repository is also queued again the next night.

## Settings

| Setting | Default | `Development` | Rules |
|---|---|---|---|
| `Scanner:Queue:Name` | | `scan-nightly` | Now exactly `scan-nightly` or `scan-ondemand` (decision 5) |
| `Scanner:Queue:PoisonMessageTimeToLiveDays` | `30` | | 1 to 365, well inside the longest expiry Azure accepts |
| `Scanner:Execution:MaxRunDurationMinutes` | `50` | | 1 to 1440 |
| `Scanner:Execution:ScanTimeoutPerRepoMinutes` | `15` | | 1 or more, and no more than `MaxRunDurationMinutes` |
| `Scanner:Execution:VisibilityTimeoutMinutes` | `65` | `1` | 1 to 10080 (Azure's limit of 7 days) |
| `Scanner:Source:LocalRoot` | | | Required in queue mode in Issue 7; a folder that exists. Set as the user environment variable `Scanner__Source__LocalRoot`, since the path differs per machine |

- `ExecutionOptions` and `SourceOptions` are new groups (ADR-022), validated at start-up and registered only in queue mode, like `QueueOptions`. `--path` needs none of them.
- 50 stops cleanly before the job's `replicaTimeout`, planned at 60 minutes in the deployment Issues. 15 is about 2.5 times ADR-012's estimate of 6 minutes per repository. 65 is `replicaTimeout` plus 5, as ADR-013 requires, so a message never reappears while the run that took it is alive. If `replicaTimeout` changes, the visibility timeout must stay above it.
- `Development` uses 1 minute so a retry can be watched in Storage Explorer; locally, one process takes the messages in turn, so a message reappearing mid-scan does no harm.

## Local setup (#34)

- **Azurite** runs on the developer's machine: `azurite --location c:\azurite_data`, installed with npm. It listens on 127.0.0.1:10000 to 10002, which `UseDevelopmentStorage=true` points at.
- **`scripts/enqueue-local.ps1`** creates any of the four queues that are missing, then adds one message. Parameters: `-RepositoryName` (required), `-Queue` (`scan-nightly` by default, or `scan-ondemand`), `-ProjectName` (`Lumina`), `-DefaultBranch` (`develop`), `-RepositoryId` (by default a GUID made from the project and repository names, so running it again lands on the same `Repositories` row), and `-Malformed`, which sends `{ "hello": 1 }` instead. It uses the Azure CLI's `az storage` commands against Azurite; that the message arrives as plain JSON, not Base64, is checked when the script is built.
- **Storage Explorer:** when adding a message by hand, untick "Encode message body in Base64", or the message is malformed.

## README

- **Configuration:** the new settings, and `Scanner:Queue:Name` limited to the two lanes.
- **Scan from the queue locally**, a new section: start Azurite, set `Scanner__Source__LocalRoot`, run `enqueue-local.ps1`, then `dotnet run --project src/RepoScanner.Scanner`. Includes the Base64 checkbox and how to watch a retry and a poisoning.
- **Exit codes:** queue mode's meaning of 0, 1 and 2 (decision 17).
- **Known limitations:** the limits below.

## Testing

Each task is written test first. `dotnet test` still needs no Azurite and no SQL Server: the loop gets a `FakeScanQueue`, `QueueConsumer` gets a `FakeQueueClient` (Azure's `QueueClient` can be subclassed for tests, and `QueuesModelFactory` makes its messages), time comes from `FakeTimeProvider`, and the database is in-memory SQLite as today.

| Task | Tests |
|---|---|
| #29 | A valid message; field names in another case; an extra field ignored; each field missing, empty, of the wrong type or all-zero GUID; `schemaVersion` 2; a body that is not JSON; the same message in Base64; `repositoryName` of `..`, or holding `\` or `/`; `defaultBranch` of `refs/heads/develop`; `problem` never contains the body |
| #30 | One message asked for, with the visibility timeout; empty queue gives null; delete by ID and pop receipt; a delete that finds the message gone logs a warning; poisoning sends the same body with a 30-day expiry and then deletes; a failed send deletes nothing; `QueueNotFound` from either queue gives `ScanQueueNotFoundException` naming it; clients built from a connection string and from a service URI; `QueueOptions` accepts only the two lane names and maps each to its trigger |
| #31 | The moved scan tests pass against `RepositoryScanRunner`, changed only in how it is built; `LocalScanRunner` still describes the folder and saves under `Local`; a missing folder marks the run Failed; the time limit marks it Failed with its message; closing the workspace on success and failure. `LocalFolderSource`: the folder inside `LocalRoot`, a missing one, a name that would leave `LocalRoot`. The loop: empty queue stops at once; one message gives one Completed ScanRun and is deleted; ten messages are scanned in order; a failed scan leaves its message; no new message is started with less than 15 minutes left; a malformed message and a 4th pick-up are poisoned; the trigger comes from the queue name; the job stopping ends the loop. `ExecutionOptions` and `SourceOptions` rules. Every existing `--path` test in `LocalModeTests` passes unchanged |
| #33 | `GetRunningAsync` returns only that repository's Running rows. A Running row 20 minutes old skips and deletes the message with no new ScanRun; 21 minutes old is marked Failed as abandoned and the repository is scanned; a 4th pick-up marks a crash's old Running row Failed but leaves a young one |
| #34 | The script is run by hand against Azurite: queues created once, a message scanned, and `-Malformed` poisoned |
| #35 | One test per success criterion through `Program`, with the fake queue and SQLite: a message gives a ScanRun with findings and exit 0; an empty queue exits 0; a malformed message is in the poison queue; a live Running scan skips; a stale one is taken over. Also: a missing queue exits 2 with one line |

- The 80% line coverage gate on the Scanner project covers the new classes.
- **Manual check** with Azurite, Storage Explorer and SSMS: a message for `RepoScanner` is Completed and deleted; an empty queue exits within seconds; `-Malformed` lands in `scan-nightly-poison`; a message for `Ghost` shows `DequeueCount` 1, 2 and 3 over three runs a minute apart and is poisoned on the 4th; a ScanRun set to Running in SSMS makes a message skip, and with `StartedUtc` 30 minutes ago it is taken over.

## Order of work

One task at a time, stopping after each for review, then a commit whose message starts "This commit" and ends in its task number (ADR-020):

1. **#28** This design document.
2. **#29** `ScanRequest` and `ScanRequestReader`.
3. **#30** The packages, `IScanQueue`, `QueueConsumer`, `QueueClients`, the `QueueOptions` changes and the ADR-013 amendment.
4. **#31** `RepositoryScanRunner` moved out of `LocalScanRunner`, the workspace and `LocalFolderSource`, `ExecutionOptions` and `SourceOptions`, the loop and its registration.
5. **#33** `GetRunningAsync`, the skip and take-over rules, and ADR-027.
6. **#34** `scripts/enqueue-local.ps1` and the README.
7. **#35** The acceptance tests through `Program`.

## Not in Issue 7

| Item | Where it is on the board |
|---|---|
| Cloning, deleting the clone, and the commit SHA on a scan run | Issue 8 |
| The enqueuer, which lists repositories through the Azure DevOps REST API | Not in this repository |
| "Scan now" in the dashboard, and disabling it while a scan is Running | The dashboard, not this Epic |
| Docker Compose and the scanner container | Issue 9 (moved from task #34 on 2026-09-29) |
| The correlation ID on every log line, the run summary and the poison alert | Issue 10 |
| `replicaTimeout`, `maxExecutions`, the scale rule and the identity's queue roles | The deployment Issues |
| Replaying poisoned messages automatically, or a dashboard page for them | Not on the board: "Scan now" and Storage Explorer's **Move** are enough for a first release |

## Known limitations

- In Azure, a scan that crashes is retried only when its message reappears, about 65 minutes later. For "Scan now" the dashboard shows the Running row turning Failed on the next pick-up, and the user can click again.
- A skipped request is not kept. If the scan it gave way to then fails, the repository waits for the next night or another "Scan now".
- Two runs that check the same repository in the same instant can both scan it. The findings are not duplicated, but there are two ScanRuns.
- A malformed message appears only in the log and the poison queue, not in the dashboard.
- A repository renamed since the message was sent is looked for under its old name. In Issue 7 that folder is not found; from Issue 8 the clone fails. The next nightly message carries the new name.
- In Issue 7, queue mode scans local folders only, so `repositoryName` must match a folder under `LocalRoot`.
