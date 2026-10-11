# Issue 13: Duplicate scan requests, implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A scan request whose work is already done is deleted without a scan: when a well-formed message's repository has a Completed or PartiallyCompleted scan run from the same lane that started after the message was queued, the message is deleted, logged as already done and counted on the run's last line.

**Architecture:** `ScanQueueMessage` gains `InsertedOn` from Azure's `QueueMessage.InsertedOn`. `IScanRunRepository` gains one query for the latest good run from a lane started after a time. `ScanOrchestrator.HandleRequestAsync` asks it first, before the poison check and the ADR-027 Running check. Everything else is tests, Issue 7's design doc, ADR-027, the README and the board.

**Tech Stack:** .NET 10, EF Core over in-memory SQLite in tests, `Azure.Storage.Queues` 12.27.1, NUnit 4, `FakeLogger`, `FakeTimeProvider`, the `az` CLI for the board.

**Spec:** Issue 13 (#76) on the board, plus the decisions agreed in chat on 2026-10-04, listed below. There is no separate design doc: this Issue amends Issue 7's, [docs/design/2026-09-29-queue-consumer-and-execution-loop.md](../../design/2026-09-29-queue-consumer-and-execution-loop.md), and [ADR-027](../../adr/ADR-027-skip-a-repository-being-scanned-take-over-an-abandoned-run.md).

## Decisions

| # | Question | Decision |
|---|---|---|
| 1 | When is a request already done | The repository (matched on `AzureDevOpsId`) has a scan run with status `Completed` or `PartiallyCompleted`, from the **same lane** (`TriggerType` equals the queue's `Trigger`), whose `StartedUtc` is strictly after the message's `InsertedOn`. `Failed` and `Running` never count |
| 2 | Same lane only | Agreed 2026-10-04, changing the Issue's wording ("its repository has a ScanRun"). A nightly scan never satisfies "Scan now", and the other way round, so a "Scan now" click always gets its own on-demand scan run |
| 3 | Where the check runs | First in `HandleRequestAsync`, inside the message's log scope: before the 4th-pick-up poison check, so a request already done is never poisoned, and before the ADR-027 Running check |
| 4 | What happens | The message is deleted, one Information line is logged, and no scan run is written. Running rows older than the abandoned age are left for the next real scan of the repository to take over |
| 5 | No `InsertedOn` | Not done: the check is skipped, with no database call |
| 6 | Time comparison | Strict `>`, no margin, in UTC. Clock differences between the storage server and the container cost at most one scan too many or too few seconds, both harmless |
| 7 | The count | Its own: `already done`, between `skipped` and `poisoned` on the last line. `skipped` keeps meaning "another run is scanning it" |
| 8 | A database error during the check | Not caught: the run stops with exit 1 and the message is left, as for the ADR-027 check |
| 9 | The gap found in chat | A push followed by "Scan now" while a scan of that repository is Running is skipped by ADR-027, and that scan may have cloned before the push. Not fixed here: raised as Issue 14 and listed as a known limitation |

## Global Constraints

- Branch: `feature/76-duplicate-scan-requests`, from an up-to-date `develop`.
- **Commits:** one per task, only after the user has reviewed that task's diff. The message starts `This commit` and ends with `#77`. It never mentions Claude, Opus or any AI tool: no `Co-Authored-By` line. This plan is committed with Task 1.
- **Board:** Issue #76 and task #77 go to Doing at the start (Task 0). #77 goes to Done when the last task's commit is made; #76 stays Doing until its pull request is merged. Push and open a pull request only when the user asks. States: `az boards work-item update --org https://dev.azure.com/Blacklight-Internal --id <id> --state <Doing|Done>`.
- **Board text and new work items** use `az rest` with the body in a UTF-8 file without a BOM, passed as `@file` (see Task 6).
- **Exact strings:**
  - Already-done line: `Already done: {ProjectName}/{RepositoryName} from message {MessageId} was scanned by scan run {ScanRunId}, which started after the message was queued at {InsertedOn}`
  - Last line: `Stopped taking messages from {QueueName}: {Reason}. {ScannedCount} scanned, {FailedCount} failed, {SkippedCount} skipped, {AlreadyDoneCount} already done, {PoisonedCount} poisoned`
- **Code conventions (ADR-021):** classes `sealed` or `static`; explicit types, no `var`; block bodies; `using (...) { }` blocks; a comment giving the reason for each non-obvious line, citing `Issue 13` where the rule comes from. Tests: NUnit, `Method_Condition_Result` names, `Assert.EnterMultipleScope()` for several asserts.
- No em dash (U+2014) in any file. British English.
- `dotnet build RepoScanner.sln` has zero warnings and `dotnet test RepoScanner.sln` passes with the coverage gate at the end of every task. Filtered runs add `-p:CollectCoverage=false`.
- **Clocks in tests:** `RepositoryScanRunner` saves `StartedUtc` from `DateTime.UtcNow`, not the `TimeProvider`. In `ScanOrchestratorTests` the fake clock reads 2026-10-01 02:00, so a run the test causes always starts after any `InsertedOn` taken from `_time`. Seeded runs get their `StartedUtc` from `_time`. This is not changed here.

## Review Focus

Inputs the Issue does not name, most likely to bite first. Each has a test in the task named.

1. A duplicate that arrives while the first copy is still being scanned: its run is `Running`, so it is not already done, and ADR-027 skips it. It counts as `skipped`, not `already done` (Task 3).
2. A good run that started at exactly `InsertedOn`: not done, since the comparison is strict (Task 2 and Task 3).
3. `InsertedOn` in another offset, such as `+01:00`: compared in UTC, not by its local clock reading (Task 3).
4. A newer `Failed` run after an older good run, both after `InsertedOn`: still done, since the good run covers the request (Task 2).
5. A good run of another repository, or of this repository from the other lane: not done (Task 2 and Task 3).

---

### Task 0: Board and branch

- [ ] **Step 1:** `git checkout develop` then `git pull`, so Issue 11's merge is in.
- [ ] **Step 2:** `git checkout -b feature/76-duplicate-scan-requests`
- [ ] **Step 3:** Set #76 and #77 to Doing. Expected: both show `"System.State": "Doing"`.

### Task 1: The message carries when it was queued

**Files:**
- Modify: `src/RepoScanner.Scanner/Queue/ScanQueueMessage.cs`
- Modify: `src/RepoScanner.Scanner/Queue/QueueConsumer.cs` (`ReceiveAsync`'s `return`)
- Modify: `tests/RepoScanner.Scanner.Tests/Queue/FakeQueueClient.cs` (`Add`)
- Modify: `tests/RepoScanner.Scanner.Tests/Queue/FakeScanQueue.cs` (`Add`)
- Test: `tests/RepoScanner.Scanner.Tests/Queue/QueueConsumerTests.cs`

**Interfaces:**
- Produces: `public sealed record ScanQueueMessage(string MessageId, string PopReceipt, string Body, long DequeueCount, DateTimeOffset? InsertedOn = null)`. The default keeps every existing `new ScanQueueMessage(...)` call compiling.
- Produces: `FakeScanQueue.Add(string body, long dequeueCount = 1, DateTimeOffset? insertedOn = null)` and `FakeQueueClient.Add(string messageId, string popReceipt, string body, long dequeueCount, DateTimeOffset? insertedOn = null)`.

- [ ] **Step 1: Write the failing test** `ReceiveAsync_CarriesTheTimeTheMessageWasQueued`: add a message to the fake client with `insertedOn: new DateTimeOffset(2026, 10, 1, 1, 0, 0, TimeSpan.Zero)`. Assert the received `message.InsertedOn` equals it.
- [ ] **Step 2:** Run `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~QueueConsumerTests" -p:CollectCoverage=false`. Expected: does not compile, because `InsertedOn` and the `Add` parameter are missing.
- [ ] **Step 3:** Add `InsertedOn` to the record, with the comment that it is Azure's time and the duplicate check compares it with a run's start (Issue 13). `QueueConsumer` passes `message.InsertedOn`. `FakeQueueClient.Add` passes `insertedOn:` to `QueuesModelFactory.QueueMessage`'s overload that takes `nextVisibleOn`, `insertedOn` and `expiresOn`. `FakeScanQueue.Add` passes it to the record.
- [ ] **Step 4:** Run the same filter. Expected: PASS, including the existing `ReceiveAsync` equality test, whose message has no `InsertedOn`.
- [ ] **Step 5:** Full build and test. Show the diff to the user. Commit once reviewed, together with this plan.

### Task 2: The "already done" query

**Files:**
- Modify: `src/RepoScanner.Core/Persistence/IScanRunRepository.cs`
- Modify: `src/RepoScanner.Data/Repositories/ScanRunRepository.cs`
- Modify: `tests/RepoScanner.Scanner.Tests/ScanOrchestratorTests.cs` (`UnreachableScanRunRepository` throws from the new method too)
- Test: `tests/RepoScanner.Data.Tests/Repositories/ScanRunRepositoryTests.cs`

**Interfaces:**
- Produces: `Task<ScanRun?> GetSuccessfulRunStartedAfterAsync(Guid azureDevOpsId, ScanTrigger trigger, DateTime startedAfterUtc, CancellationToken cancellationToken)`. It returns the repository's latest-started `Completed` or `PartiallyCompleted` run with that `TriggerType` and `StartedUtc > startedAfterUtc`, or `null`. Interface comment: matched on `AzureDevOpsId`; the queue loop deletes a request it covers (Issue 13).

- [ ] **Step 1: Write the failing tests.** Give the existing `RunStarted` helper an optional `ScanTrigger trigger = ScanTrigger.Nightly` parameter. With `StartedUtc` as T:
  - `GetSuccessfulRunStartedAfterAsync_ReturnsTheLatestGoodRunFromThatLane`: this repository has Nightly runs `Completed` T+10, `PartiallyCompleted` T+20, `Failed` T+30 and `Running` T+40, plus an `OnDemand` `Completed` at T+50. Another repository has a Nightly `Completed` at T+60. A query for `(AzureDevOpsId, Nightly, T)` returns the T+20 run's `Id`.
  - `GetSuccessfulRunStartedAfterAsync_IgnoresARunThatStartedAtOrBeforeTheTime`: Nightly `Completed` runs at T and T-1. Returns `null`.
  - `GetSuccessfulRunStartedAfterAsync_ForARepositoryNotStoredYet_ReturnsNone`: returns `null`.
- [ ] **Step 2:** Run `dotnet test tests/RepoScanner.Data.Tests --filter "FullyQualifiedName~GetSuccessfulRunStartedAfterAsync" -p:CollectCoverage=false`. Expected: does not compile.
- [ ] **Step 3:** Implement in `ScanRunRepository`, in the shape of `GetRunningAsync`: `Where` on repository, trigger, the two statuses and `StartedUtc > startedAfterUtc`, then `OrderByDescending(run => run.StartedUtc)` and `FirstOrDefaultAsync`. Add the method to `UnreachableScanRunRepository`, throwing `InvalidOperationException("The database cannot be reached")`.
- [ ] **Step 4:** Run the same filter. Expected: 3 PASS.
- [ ] **Step 5:** Full build and test. Review, then commit.

### Task 3: The loop drops a request already done

**Files:**
- Modify: `src/RepoScanner.Scanner/ScanOrchestrator.cs` (`HandleRequestAsync`, `LogStopped`, `RunCounts`, the class comment)
- Test: `tests/RepoScanner.Scanner.Tests/ScanOrchestratorTests.cs`

**Interfaces:**
- Consumes: `ScanQueueMessage.InsertedOn` (Task 1), `IScanRunRepository.GetSuccessfulRunStartedAfterAsync` (Task 2), `_queueOptions.Trigger`.
- Produces: `RunCounts.AlreadyDone`, and the two exact strings in Global Constraints.

- [ ] **Step 1: Write the failing tests.** Generalise `SeedRunningRun` into `SeedRun(Guid azureDevOpsId, ScanRunStatus status, ScanTrigger trigger, TimeSpan startedAgo)`. Keep `SeedRunningRun` as a call to it with `Running` and `OnDemand`. "Queued N ago" below means `insertedOn: _time.GetUtcNow() - N`. Find the already-done line with `Message.StartsWith("Already done", StringComparison.Ordinal)`.
  - `RunAsync_WithTwoCopiesOfOneRequest_ScansOnceAndDeletesBoth`: two identical bodies for `OrdersApiId`, both queued 1 minute ago. Assert: one scan run, `scanner.CallCount` 1, `_queue.Deleted` holds both messages, and the last line has `ScannedCount` "1" and `AlreadyDoneCount` "1". This is Issue criterion 1.
  - `RunAsync_WhenAGoodScanStartedAfterTheMessageWasQueued_DeletesItWithoutScanning`, with `[TestCase(ScanRunStatus.Completed)]` and `[TestCase(ScanRunStatus.PartiallyCompleted)]`: seed a Nightly run 5 minutes ago, with the message queued 10 minutes ago. Assert: the message is deleted; there's still one scan run; `CallCount` 0; the line's `ScanRunId` is the seeded ID; the line's scope `CorrelationId` is the message's.
  - `RunAsync_WhenTheScanAfterTheMessageFailed_ScansAgain`: a Nightly `Failed` run 5 minutes ago, with the message queued 10 minutes ago. Assert: `CallCount` 1 and `AlreadyDoneCount` "0". This is Issue criterion 2.
  - `RunAsync_WhenTheFirstCopyIsStillBeingScanned_CountsItAsSkipped`: a Nightly `Running` run 2 minutes ago, with the message queued 10 minutes ago. Assert: `SkippedCount` "1" and `AlreadyDoneCount` "0". This is Review Focus 1.
  - `RunAsync_WhenTheGoodScanStartedBeforeTheMessageWasQueued_ScansIt`, with `[TestCase(10)]` (the run is older) and `[TestCase(5)]` (exactly the queued time): seed a Nightly `Completed` run that many minutes ago, with the message queued 5 minutes ago. Assert: `CallCount` 1. This is Review Focus 2.
  - `RunAsync_WhenTheGoodScanCameFromTheOtherQueue_ScansIt`: an `OnDemand` `Completed` run 5 minutes ago, the message queued 10 minutes ago, on the nightly queue. Assert: `CallCount` 1. This is decision 2.
  - `RunAsync_OnTheFourthPickUpOfARequestAlreadyDone_DeletesItInsteadOfPoisoning`: `dequeueCount: 4` and a Nightly `Completed` run after it. Assert: the message is deleted, `_queue.Poisoned` is empty and there are no Warning records. This is Issue criterion 3.
  - `RunAsync_WithNoQueuedTime_ScansIt`: a Nightly `Completed` run 5 minutes ago, with no `insertedOn`. Assert: `CallCount` 1.
  - `RunAsync_WithAQueuedTimeInAnotherOffset_ComparesInUtc`: a Nightly `Completed` run at 01:55Z (5 minutes ago). The message is queued at `new DateTimeOffset(2026, 10, 1, 2, 50, 0, TimeSpan.FromHours(1))`, which is 01:50Z. Assert: already done. This is Review Focus 3.
  - Update the exact last line in `RunAsync_WhenAzureDevOpsRefusesTheToken_StopsLeavingThatMessageAndTheRest` to `... 0 scanned, 1 failed, 0 skipped, 0 already done, 0 poisoned`.
- [ ] **Step 2:** Run `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~ScanOrchestratorTests" -p:CollectCoverage=false`. Expected: the new tests and the updated line fail.
- [ ] **Step 3: Implement.** At the top of `HandleRequestAsync`, after `scanRuns` is resolved, if `message.InsertedOn` has a value, call `GetSuccessfulRunStartedAfterAsync(request.RepositoryId, _queueOptions.Trigger, message.InsertedOn.Value.UtcDateTime, cancellationToken)`. If a run comes back: delete the message, log the already-done line at Information with `run.Id` and `message.InsertedOn`, add 1 to `counts.AlreadyDone`, and return. The comment says why it comes before the poison check (decision 3) and why a database error is not caught (decision 8). Add `AlreadyDone` to `RunCounts` and to `LogStopped`'s template. Add one sentence to the class comment.
- [ ] **Step 4:** Run the same filter. Expected: all PASS.
- [ ] **Step 5:** Full build and test. Review, then commit.

### Task 4: Through the whole program

**Files:**
- Test: `tests/RepoScanner.Scanner.Tests/QueueModeTests.cs`

- [ ] **Step 1: Write the test** `TwoCopiesOfOneRequest_GiveOneScanRunAndBothAreDeleted`: two `_queue.Add(Body("secrets-repo"), insertedOn: DateTimeOffset.UtcNow.AddMinutes(-1))` with the same body, then `RunAsync("scan-nightly")`. Assert: exit code 0; one scan run, `Completed`; `_queue.Deleted` holds both; one line starts `Already done` with `RepositoryName` `secrets-repo`; the `Stopped taking messages` line has `AlreadyDoneCount` "1". The real clock is used here, so the scan starts after `InsertedOn`.
- [ ] **Step 2:** Run `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~QueueModeTests" -p:CollectCoverage=false`. Expected: PASS, since Tasks 1 to 3 are in. If it fails, the wiring through `Program` is wrong: fix the code, not the test.
- [ ] **Step 3:** Full build and test. Review, then commit.

### Task 5: Documents

**Files:**
- Modify: `docs/design/2026-09-29-queue-consumer-and-execution-loop.md`
- Modify: `docs/adr/ADR-027-skip-a-repository-being-scanned-take-over-an-abandoned-run.md`
- Modify: `README.md`

- [ ] **Step 1: Issue 7's design doc.**
  - Decision 11's reason: a crash between saving and deleting no longer scans twice. The message reappears and is deleted as already done (Issue 13).
  - Decision 16: the new check comes first.
  - Add decision 25, same lane only, with its reason from decision 2 above.
  - The loop: a new step between 4 and 5, "Already done (Issue 13): delete it, then go back to 2".
  - The last line's counts include `already done`.
  - Known limitations: add the Issue 14 gap from decision 9.
- [ ] **Step 2: ADR-027.** Add an `## Amendments` section in ADR-011's form: `**2026-10-09:** ...`. It records the check, its place before this ADR's Running check and the poison check, same lane only, `Failed` and `Running` never counting, and the Issue 14 gap under Consequences.
- [ ] **Step 3: README.** Update the `Run it` example last line to `1 scanned, 0 failed, 0 skipped, 0 already done, 0 poisoned`. Add a bullet beside the "being scanned" one for an already-done request. Add the Issue 14 gap to Known limitations, under "The queue loop". Then grep the repository for `skipped, 0 poisoned` and `SkippedCount`, and update any copy left.
- [ ] **Step 4:** Check for em dashes with `grep -rnP '\x{2014}' docs README.md`. Expected: no output. Review, then commit.

### Task 6: Board

- [ ] **Step 1: Create Issue 14** with `az rest --method post --resource 499b84ac-1321-427f-aa17-267ca6975798` to `https://dev.azure.com/Blacklight-Internal/Lumina/_apis/wit/workitems/$Issue?api-version=7.1`, with header `Content-Type=application/json-patch+json` and the body as `@<file>`. `$Issue` is part of the URL: in PowerShell, write it as `` `$Issue `` so it isn't read as a variable. Fields:
  - Title: `14. Scan now while a scan is running`
  - Parent #1 (`System.LinkTypes.Hierarchy-Reverse`)
  - Area `Lumina\Scanner`, iteration `Lumina\Sprint 1`, tags `feature; scanner`, `Microsoft.VSTS.Common.StackRank` `14000`, assigned to the user
  - Description in #76's layout:
    - **Why:** the gap in decision 9.
    - **Notes:** a fix would keep a skipped request instead of deleting it, so it runs after the live scan ends.
    - **Acceptance criteria:** a "Scan now" request that arrives while its repository is being scanned is scanned once that scan ends; and ADR-027's "a skipped request is not kept" is amended.
- [ ] **Step 2:** Add one line to #76's Notes: "Same lane only: a nightly scan never satisfies Scan now (agreed 2026-10-04)." Read the description first and change only that.
- [ ] **Step 3:** Check the team backlog API (`Lumina%20Team/_apis/work/backlogs/Microsoft.RequirementCategory/workItems`). Expected: Issue 14 sits after 13 and before the Enqueuer Issues.
- [ ] **Step 4:** Once the last commit is made, set #77 to Done. #76 stays Doing.
