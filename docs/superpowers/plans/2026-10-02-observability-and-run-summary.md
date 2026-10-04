# Issue 10: Observability and run summary, implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every line of a scan carries the message's `CorrelationId`, `ProjectName` and `RepositoryName`; each scanner logs one line with its status, time and findings; each scan run ends with exactly one summary line whatever its outcome; and outside Development both loggers write one JSON object per line, so one Log Analytics search returns a whole scan.

**Architecture:** An `ILogger` scope (`ScanLogScope`) opened per message in `ScanOrchestrator` and per scan in `RepositoryScanRunner` flows to every logger below it, and Serilog turns it into properties. `RepositoryScanRunner` logs the per-scanner and summary lines from the `ScannerResult`s and `ScanRunCompletion` it already builds. `ConsoleLogFormat` decides text or JSON for both the start-up logger and the host's logger; JSON is `RenderedCompactJsonFormatter` (CLEF).

**Tech Stack:** .NET 10, Serilog 4.3.0 with `Serilog.Extensions.Hosting` 10.0.0 and `Serilog.Sinks.Console` 6.1.1, the new `Serilog.Formatting.Compact` 3.0.0, `Microsoft.Extensions.Diagnostics.Testing` (`FakeLogger`), NUnit 4, `System.Text.Json` in the tests, Windows PowerShell 5.1 for `scripts/test-container.ps1`.

**Spec:** [docs/design/2026-10-02-observability-and-run-summary.md](../../design/2026-10-02-observability-and-run-summary.md). Read it before starting; this plan argues from it. The design was committed as `231a383` (#61).

## Global Constraints

- Branch: `feature/61-observability-and-run-summary` (already created from `develop`).
- **Commits:** one per task, only after the user has reviewed that task's diff. The message starts `This commit` and ends with the task number, for example `#62`. It never mentions Claude, Opus or any AI tool: no `Co-Authored-By` line.
- **Board:** set the task to Doing when its plan task starts and to Done when its commit is made: `az boards work-item update --org https://dev.azure.com/Blacklight-Internal --id <id> --state <Doing|Done>`. Issue #61 stays Doing until its pull request is merged. Push and open a pull request only when the user asks.
- **Order:** #62, #63, #64, #65, #100.
- **Property names, exactly:** `CorrelationId`, `ProjectName`, `RepositoryName` (scope); `ScannerType`, `ScannerStatus`, `ScannerDurationMs`, `FindingCount` (per-scanner line); `CloneDurationMs` (clone line); `ScanRunId`, `ScanRunStatus`, `Trigger`, `CommitSha`, `DurationMs`, `FoundCount`, `NewCount`, `ReopenedCount`, `ResolvedCount`, `SuppressionExpiredCount`, `ScannerStatuses`, `ErrorMessage` (summary). No `{Status}` property anywhere afterwards.
- **Templates, exactly:**
  - Per scanner: `{ScannerType} scanner {ScannerStatus} in {ScannerDurationMs} ms: {FindingCount} findings`
  - Summary: `Scan run {ScanRunId} {ScanRunStatus} ({Trigger}) for {ProjectName}/{RepositoryName} at {CommitSha} in {DurationMs} ms: {FoundCount} found, {NewCount} new, {ReopenedCount} reopened, {ResolvedCount} resolved, {SuppressionExpiredCount} suppressions expired. Scanners: {ScannerStatuses}`, and when not Completed the same with `. Error: {ErrorMessage}` appended.
- **The setting:** `Scanner:Logging:Format`, `Text` or `Json` in any case (Review Focus 4); not set means `Text` in Development and `Json` elsewhere; any other value exits 2.
- **Package:** `Serilog.Formatting.Compact` `3.0.0`, central version in `Directory.Packages.props`.
- **Noise overrides:** `System.Net.Http.HttpClient` and `Polly` to `LogEventLevel.Warning`, beside the existing `Microsoft` one.
- **Searches:** table `ContainerAppConsoleLogs`, column `Log`, unpacked with `parse_json(Log)`, as in the spec's Searches section.
- **Lines that must keep their wording** (the alerts search them): `Message {MessageId} moved to {PoisonQueueName}: {Reason}...` (both poison lines) and `Azure DevOps refused the token, scanner stopped: {Problem}`.
- **Code conventions (ADR-021):** classes `sealed` or `static`; explicit types, no `var`; block bodies; `using (...) { }` blocks; a comment giving the reason for each non-obvious line. Tests: NUnit, `Method_Condition_Result` names, `Assert.EnterMultipleScope()` for several asserts.
- **Windows PowerShell 5.1** for the script: no `&&`, `||`, `??` or `?.`; never `2>&1` on a native command while `$ErrorActionPreference` is `Stop`.
- **Secrets:** `.env` is never staged, printed or read back. The script never prints a value from `.env`.
- No em dash (U+2014) in any file. British English.
- `dotnet build RepoScanner.sln` has zero warnings and `dotnet test RepoScanner.sln` passes with the coverage gate at the end of every task. Filtered runs add `-p:CollectCoverage=false`.

## Review Focus

Inputs the spec does not mention, most likely to bite first. Each has a test in the task named.

1. **A value with quotes or line breaks**, such as git's error output or an exception's stack trace: each event must stay one line of valid JSON, or `parse_json` drops it. Task 4: `RunAsync_AFailureWithQuotesAndLineBreaks_StaysOneJsonLine`.
2. **The PAT hidden by JSON escaping:** the Issue 8 leak test searches the raw output, so a PAT escaped inside a JSON string would pass unseen. Task 4: the leak test also searches every unescaped string value.
3. **One message's ID on the next message's lines**, if a failure path leaves the scope open. Task 1: `RunAsync_AFailedScanThenAnother_EachLineCarriesItsOwnMessage`.
4. **`json` or `TEXT` in lower or upper case**, which .NET settings usually accept: taken as `Json` or `Text`; `Xml` exits 2 naming both values. Task 4: the case `TestCase`s and `Main_WithAFormatThatIsNeither_ExitsTwoWithOneLine`. The design's decision 9 and its `ConsoleLogFormat` sketch are updated to say any case.
5. **A retry hidden by the noise override:** nuget.org failing must still show Polly's retries. Task 4: `Path_WhenNuGetOrgGoesDown_LogsTheRetriesButNotEachRequest`. If Polly logs retries below Warning, stop and ask the user before changing decision 11.

---

### Task 1: The scan's logging scope (#62)

**Files:**
- Create: `src/RepoScanner.Scanner/Logging/ScanLogScope.cs`, `tests/RepoScanner.Scanner.Tests/Logging/LogRecordValues.cs`
- Modify: `src/RepoScanner.Scanner/ScanOrchestrator.cs` (`HandleAsync`), `src/RepoScanner.Scanner/RepositoryScanRunner.cs` (`RunAsync`)
- Test: `tests/RepoScanner.Scanner.Tests/RepositoryScanRunnerTests.cs`, `tests/RepoScanner.Scanner.Tests/ScanOrchestratorTests.cs`

**Interfaces:**
- Produces: `ScanLogScope.For(Guid correlationId, string projectName, string repositoryName) -> IReadOnlyDictionary<string, object>` with keys `CorrelationId`, `ProjectName`, `RepositoryName`.
- Produces (tests): `LogRecordValues.Scope(FakeLogRecord record, string name) -> object?`, the innermost scope value with that key, null when none; `LogRecordValues.State(FakeLogRecord record, string name) -> object?`, the raw value passed to the template (from `record.State` as `IEnumerable<KeyValuePair<string, object?>>`), for values `GetStructuredStateValue` turns into text. Tasks 2 and 3 use both.

- [ ] **Step 1: Set #62 to Doing**

- [ ] **Step 2: Write the failing tests**

`RepositoryScanRunnerTests`: add `private static readonly Guid CorrelationId = new Guid("9a7b3c1d-2e4f-4a6b-8c0d-1e2f3a4b5c6d");` and pass it from the `RunAsync` helper instead of `Guid.NewGuid()`.

```csharp
[Test]
public async Task RunAsync_EveryLineItLogs_CarriesTheScansCorrelationIdAndRepository()
{
    await RunAsync(new StubScanner(Raw("SEC003", "src/appsettings.json", 3, "hash-a")));

    IReadOnlyList<FakeLogRecord> records = _logger.Collector.GetSnapshot();
    using (Assert.EnterMultipleScope())
    {
        Assert.That(records, Is.Not.Empty);
        Assert.That(records.Select(record => LogRecordValues.Scope(record, "CorrelationId")), Is.All.EqualTo(CorrelationId));
        Assert.That(records.Select(record => LogRecordValues.Scope(record, "ProjectName")), Is.All.EqualTo("Lumina"));
        Assert.That(records.Select(record => LogRecordValues.Scope(record, "RepositoryName")), Is.All.EqualTo("orders-api"));
    }
}

[Test]
public async Task RunAsync_WhenAScannerFails_ItsWarningsCarryTheCorrelationId()
// FailingPackageScanner beside a StubScanner: every Warning record has Scope "CorrelationId" == CorrelationId
```

`ScanOrchestratorTests`:

```csharp
[Test]
public async Task RunAsync_TheLineAboutAReadMessage_CarriesItsCorrelationIdAndRepository()
// orders-api with correlationId 5b1c6f3e-8a2d-4e7f-9c1a-2d3e4f5a6b7c: the record whose Message starts "Message " has
// Scope CorrelationId == that Guid, ProjectName "Lumina", RepositoryName "orders-api"

[Test]
public async Task RunAsync_AFailedScanThenAnother_EachLineCarriesItsOwnMessage()   // Review Focus 3
// Ghost (no folder, so it fails) with correlation A, then orders-api with correlation B: the failure Warning has
// Scope CorrelationId == A; orders-api's "Message " record has B; the "Stopped taking messages" record has no scope

[Test]
public async Task RunAsync_WithAMalformedMessage_ItsLineHasNoScope()
// { "hello": 1 }: the Warning has Scope "CorrelationId" == null

[Test]
public async Task RunAsync_TheSkippedAndPoisonedLines_CarryTheirMessagesCorrelationId()
// [TestCase] a live Running run (startedAgo 20 minutes) gives the "Skipped" record; dequeueCount 4 gives the poison
// Warning: each has Scope "CorrelationId" == the message's
```

- [ ] **Step 3: Run them to verify they fail**

Run: `dotnet test RepoScanner.sln --filter "FullyQualifiedName~RepositoryScanRunnerTests|FullyQualifiedName~ScanOrchestratorTests" -p:CollectCoverage=false`
Expected: the new tests fail to compile (`ScanLogScope`, `LogRecordValues` not defined), then, once `LogRecordValues` exists, fail on null scope values.

- [ ] **Step 4: Write `ScanLogScope` and open it**

`ScanLogScope` as in the spec's code block, in namespace `RepoScanner.Scanner.Logging`. In `HandleAsync`, open `_logger.BeginScope(ScanLogScope.For(request.CorrelationId, request.ProjectName, request.RepositoryName))` straight after `TryRead` succeeds, around both the `IServiceScope` block and the `DeleteAsync`, so it closes on every return. In `RunAsync`, open it from `repository.ProjectName` and `repository.Name` around the whole body. The existing templates stay as they are.

- [ ] **Step 5: Run the tests to verify they pass**

Same filter. Expected: PASS, including every existing test in both classes.

- [ ] **Step 6: Full build and tests**

`dotnet build RepoScanner.sln` (zero warnings), then `dotnet test RepoScanner.sln`. Expected: all pass, coverage gate met.

- [ ] **Step 7: Stop for review, then commit and set #62 to Done**

```bash
git add src/RepoScanner.Scanner/Logging/ScanLogScope.cs src/RepoScanner.Scanner/ScanOrchestrator.cs src/RepoScanner.Scanner/RepositoryScanRunner.cs tests/RepoScanner.Scanner.Tests/Logging/LogRecordValues.cs tests/RepoScanner.Scanner.Tests/RepositoryScanRunnerTests.cs tests/RepoScanner.Scanner.Tests/ScanOrchestratorTests.cs
git commit -m "This commit puts the message's CorrelationId, ProjectName and RepositoryName on every line of its scan: ScanLogScope opens an ILogger scope once a message is read, kept open until it is deleted, and again for each scan, so --path scans carry it too; the clone, git, the scanners and the finding lines get it without changing, a malformed message's line has none, and one message's ID never reaches the next message's lines #62"
```

---

### Task 2: One line per scanner (#63)

**Files:**
- Modify: `src/RepoScanner.Scanner/RepositoryScanRunner.cs` (the scanner loop), `src/RepoScanner.Scanner/Sources/AzureDevOpsCloneSource.cs` (the `Cloned` line)
- Test: `tests/RepoScanner.Scanner.Tests/RepositoryScanRunnerTests.cs`, `tests/RepoScanner.Scanner.Tests/Sources/AzureDevOpsCloneSourceTests.cs`

**Interfaces:**
- Consumes: `LogRecordValues` (Task 1).
- Produces: one record per scanner that finished, with the per-scanner template.

- [ ] **Step 1: Set #63 to Doing**

- [ ] **Step 2: Write the failing tests**

```csharp
[Test]
public async Task RunAsync_LogsOneLinePerScannerWithItsStatusTimeAndFindings()
{
    await RunAsync(new StubScanner(Raw("SEC003", "a.json", 3, "hash-a"), Raw("SEC007", "a.json", 6, "hash-b")), new StubScanner() { ScannerType = ScannerType.Package });

    FakeLogRecord[] lines = _logger.Collector.GetSnapshot().Where(record => record.GetStructuredStateValue("ScannerStatus") != null).ToArray();
    using (Assert.EnterMultipleScope())
    {
        Assert.That(lines.Select(line => line.GetStructuredStateValue("ScannerType")), Is.EqualTo(new[] { "Secret", "Package" }));
        Assert.That(lines.Select(line => line.GetStructuredStateValue("ScannerStatus")), Is.All.EqualTo("Succeeded"));
        Assert.That(lines.Select(line => line.GetStructuredStateValue("FindingCount")), Is.EqualTo(new[] { "2", "0" }));
        Assert.That(lines.Select(line => int.Parse(line.GetStructuredStateValue("ScannerDurationMs")!, CultureInfo.InvariantCulture)), Is.All.GreaterThanOrEqualTo(0));
        Assert.That(lines.Select(line => line.Level), Is.All.EqualTo(LogLevel.Information));
        Assert.That(lines[0].Message, Does.StartWith("Secret scanner Succeeded in ").And.EndWith(" ms: 2 findings"));
    }
}

[Test]
public async Task RunAsync_WhenAScannerFails_ItsLineIsAWarningWithTheException()
// replaces RunAsync_WhenOneScannerFails_LogsAWarningNamingIt. FailingPackageScanner beside a StubScanner: the Package
// line is Warning, ScannerStatus "Failed", FindingCount "0", Exception is InvalidOperationException; no record's
// Message contains "the other scanners carry on"

[Test]
public async Task RunAsync_WhenTheRunIsCancelled_LogsNoLineForTheStoppedScanner()
// the cancelled-scanner setup from RunAsync_WhenTheRunIsCancelled_StopsAndMarksTheRunFailed: no record has
// ScannerStatus
```

`AzureDevOpsCloneSourceTests`, in the test that checks the `Cloned ... at <commit> in` line: that record has `CloneDurationMs` and `GetStructuredStateValue("DurationMs")` is null.

- [ ] **Step 3: Run them to verify they fail**

Run: `dotnet test RepoScanner.sln --filter "FullyQualifiedName~RepositoryScanRunnerTests|FullyQualifiedName~AzureDevOpsCloneSourceTests" -p:CollectCoverage=false`
Expected: FAIL: no record has `ScannerStatus`; the clone record has `DurationMs`.

- [ ] **Step 4: Log the line and rename the clone's property**

After each `results.Add(...)` in the loop, log the per-scanner template from that `ScannerResult`: `LogLevel.Information` with no exception when it succeeded, `LogLevel.Warning` with the caught exception when it failed. Remove `The {ScannerType} scanner failed; the other scanners carry on`. In `AzureDevOpsCloneSource`, `{DurationMs}` becomes `{CloneDurationMs}`; the rendered text is unchanged.

- [ ] **Step 5: Run the tests to verify they pass**

Same filter. Expected: PASS.

- [ ] **Step 6: Full build and tests**

As in Task 1.

- [ ] **Step 7: Stop for review, then commit and set #63 to Done**

```bash
git add src/RepoScanner.Scanner/RepositoryScanRunner.cs src/RepoScanner.Scanner/Sources/AzureDevOpsCloneSource.cs tests/RepoScanner.Scanner.Tests/RepositoryScanRunnerTests.cs tests/RepoScanner.Scanner.Tests/Sources/AzureDevOpsCloneSourceTests.cs
git commit -m "This commit logs one line per scanner as it finishes, {ScannerType} scanner {ScannerStatus} in {ScannerDurationMs} ms: {FindingCount} findings, from the ScannerResult already built for the database: Information when it succeeded and Warning with the exception when it failed, in place of the old failed-scanner warning; a scanner stopped by the time limit or the job logs none; and the clone line's time becomes CloneDurationMs, so DurationMs only ever means a whole scan run #63"
```

---

### Task 3: The summary line for every outcome (#64)

**Files:**
- Modify: `src/RepoScanner.Scanner/RepositoryScanRunner.cs`
- Test: `tests/RepoScanner.Scanner.Tests/RepositoryScanRunnerTests.cs`, `tests/RepoScanner.Scanner.Tests/Local/LocalModeTests.cs` (line 329)

**Interfaces:**
- Consumes: `LogRecordValues.State` (Task 1); the per-scanner `ScannerResult`s (Task 2).
- Produces: exactly one record per scan run whose Message starts `Scan run `, logged last. `ScannerStatuses` is a `Dictionary<string, string>` from `ScannerType` name to `ScannerStatus` name, in the order the scanners ran. Task 5's acceptance tests read it as a JSON object.

- [ ] **Step 1: Set #64 to Doing**

- [ ] **Step 2: Write the failing tests**

A shared helper in the test class: `private FakeLogRecord TheSummary()` asserts exactly one record's Message starts `Scan run `, that it is the last record, and returns it.

```csharp
[Test]
public async Task RunAsync_WhenItCompletes_EndsWithOneSummaryLineHoldingEveryCount()
{
    await RunAsync(OpenClone, timeLimit: null, CancellationToken.None,
        new StubScanner(Raw("SEC003", "a.json", 3, "hash-a"), Raw("SEC007", "a.json", 6, "hash-b")), new StubScanner() { ScannerType = ScannerType.Package });

    FakeLogRecord summary = TheSummary();
    using (Assert.EnterMultipleScope())
    {
        Assert.That(summary.Level, Is.EqualTo(LogLevel.Information));
        Assert.That(summary.GetStructuredStateValue("ScanRunStatus"), Is.EqualTo("Completed"));
        Assert.That(summary.GetStructuredStateValue("Trigger"), Is.EqualTo("OnDemand"));
        Assert.That(summary.GetStructuredStateValue("ProjectName"), Is.EqualTo("Lumina"));
        Assert.That(summary.GetStructuredStateValue("RepositoryName"), Is.EqualTo("orders-api"));
        Assert.That(summary.GetStructuredStateValue("CommitSha"), Is.EqualTo(CommitId));
        Assert.That(summary.GetStructuredStateValue("FoundCount"), Is.EqualTo("2"));
        Assert.That(summary.GetStructuredStateValue("NewCount"), Is.EqualTo("2"));
        Assert.That(summary.GetStructuredStateValue("ReopenedCount"), Is.EqualTo("0"));
        Assert.That(summary.GetStructuredStateValue("ResolvedCount"), Is.EqualTo("0"));
        Assert.That(summary.GetStructuredStateValue("SuppressionExpiredCount"), Is.EqualTo("0"));
        Assert.That(LogRecordValues.State(summary, "ScannerStatuses"),
            Is.EqualTo(new Dictionary<string, string> { ["Secret"] = "Succeeded", ["Package"] = "Succeeded" }));
        Assert.That(summary.Message, Does.Not.Contain("Error:"));
    }
}

[Test]
public async Task RunAsync_WhenPartiallyCompleted_TheSummaryIsAWarningWithTheError()
// replaces RunAsync_WhenOneOfTwoScannersFails_LogsAWarningForThePartiallyCompletedRun. FailingPackageScanner:
// Warning, ScanRunStatus "PartiallyCompleted", ErrorMessage "Package scanner failed: nuget.org data could not be
// downloaded", ScannerStatuses Secret Succeeded and Package Failed; no record's Message contains "partially completed:"

[TestCase("every scanner fails")]
[TestCase("the workspace cannot be opened")]
[TestCase("the time limit is reached")]
[TestCase("the run is cancelled")]
[TestCase("saving the findings fails")]
public async Task RunAsync_WhenTheRunFails_EndsWithOneFailedSummary(string how)
// each case reuses the existing test's setup for that outcome: TheSummary() is Warning, ScanRunStatus "Failed",
// FoundCount, NewCount, ReopenedCount and ResolvedCount "0", and ErrorMessage as the run saved it. The workspace case
// also has CommitSha null and an empty ScannerStatuses; the every-scanner-fails case has both scanners "Failed"

[Test]
public async Task RunAsync_NoLineHasAStatusProperty()
// a completed and a partially completed run: no record has GetStructuredStateValue("Status")
```

`LocalModeTests` line 329: `Does.Contain("partially completed")` becomes `Does.Contain("PartiallyCompleted")`.

- [ ] **Step 3: Run them to verify they fail**

Run: `dotnet test RepoScanner.sln --filter "FullyQualifiedName~RepositoryScanRunnerTests|FullyQualifiedName~LocalModeTests" -p:CollectCoverage=false`
Expected: FAIL: no summary on a failed run, and the completed one has `Status`, not `ScanRunStatus`.

- [ ] **Step 4: Log the summary from every outcome**

Add `private void LogSummary(int scanRunId, RepositoryDetails repository, ScanTrigger trigger, ScanRunCompletion completion, IReadOnlyList<ScannerResult> results, int foundCount, int suppressionExpiredCount, long durationMs)`: Information with the summary template when `completion.Status` is Completed, otherwise Warning with the `. Error: {ErrorMessage}` template; `ScannerStatuses` built from `results`. Move `results` and the `duration` stopwatch above the `try`, so the failure paths have the scanners' results so far. Call it after each `CompleteRunAsync` (Completed, PartiallyCompleted, every scanner failed) and after `RecordFailureAsync` (which returns the completion it tried to save, including when the save itself failed). On Failed, the counts are 0, as `ScanRuns` saves them. Remove the old summary line and the `Scan run {ScanRunId} partially completed` warning.

- [ ] **Step 5: Run the tests to verify they pass**

Same filter. Expected: PASS.

- [ ] **Step 6: Full build and tests**

As in Task 1.

- [ ] **Step 7: Stop for review, then commit and set #64 to Done**

```bash
git add src/RepoScanner.Scanner/RepositoryScanRunner.cs tests/RepoScanner.Scanner.Tests/RepositoryScanRunnerTests.cs tests/RepoScanner.Scanner.Tests/Local/LocalModeTests.cs
git commit -m "This commit ends every scan run with exactly one summary line, whatever the outcome: Completed, PartiallyCompleted, and Failed through every scanner failing, a workspace that cannot be opened, the time limit, the job being stopped or a failed save; it holds the run, its status, the trigger, the repository, the commit, the time from the run's start, the found, new, reopened, resolved and expired-suppression counts and each scanner's status, at Information when Completed and at Warning with the error otherwise, replacing the old summary and the partially-completed warning, with ScanRunStatus in place of Status #64"
```

---

### Task 4: JSON or text, for both loggers (#65)

**Files:**
- Create: `src/RepoScanner.Scanner/Logging/ConsoleLogFormat.cs`, `src/RepoScanner.Scanner/Configuration/LoggingOptions.cs`, `tests/RepoScanner.Scanner.Tests/LogLines.cs`, `tests/RepoScanner.Scanner.Tests/Logging/ConsoleLogFormatTests.cs`, `docs/adr/ADR-028-json-log-lines-on-stdout-outside-development.md`
- Modify: `src/RepoScanner.Scanner/Program.cs`, `src/RepoScanner.Scanner/RepoScanner.Scanner.csproj`, `Directory.Packages.props`, `docs/adr/README.md`, `README.md` (Configuration), `docs/design/2026-10-02-observability-and-run-summary.md` (decision 9 and the sketch: any case)
- Test: `tests/RepoScanner.Scanner.Tests/ProgramTests.cs`, `QueueModeTests.cs`, `Local/LocalModeTests.cs`, `Configuration/DatabaseRegistrationTests.cs`

**Interfaces:**
- Produces: `ConsoleLogFormat.IsJson(string? setting, string environmentName) -> bool` (`Text` or `Json` in any case wins; anything else falls back to `Json` unless the environment is Development) and `ConsoleLogFormat.IsJsonBeforeTheHost(string[] args) -> bool` (reads `Scanner:Logging:Format`, then the environment from `--environment`, else `DOTNET_ENVIRONMENT`, else Production, from environment variables and the command line only).
- Produces: `LoggingOptions` (`SectionName = "Scanner:Logging"`, `string? Format`, validated with `[RegularExpression("^(?i:text|json)$", ErrorMessage = "Scanner:Logging:Format must be Text or Json")]`), registered in every mode with `ValidateDataAnnotations().ValidateOnStart()`.
- Produces (tests): `LogLines.Read(string output) -> IReadOnlyList<JsonElement>` (each non-empty line parsed; a line that is not one JSON object fails the test, naming its number); `LogLines.Level(JsonElement line) -> string` (`@l`, or `Information` when absent); `LogLines.Message(JsonElement line) -> string` (`@m`); `LogLines.Value(JsonElement line, string name) -> string?` (a string as it is, anything else as its JSON, null when absent); `LogLines.Strings(JsonElement line) -> IEnumerable<string>` (every string value anywhere in it, unescaped). Task 5 uses all of them.

- [ ] **Step 1: Set #65 to Doing**

- [ ] **Step 2: Add the package**

`Directory.Packages.props`, in the Logging group: `<PackageVersion Include="Serilog.Formatting.Compact" Version="3.0.0" />`. `RepoScanner.Scanner.csproj`, after `Serilog.Sinks.Console`: `<PackageReference Include="Serilog.Formatting.Compact" />` with the comment `<!-- One JSON object per log line outside Development, for Log Analytics (ADR-028) -->`. Run `dotnet restore RepoScanner.sln`; it downloads the package from nuget.org. Expected: restores with no warnings.

- [ ] **Step 3: Write the failing tests**

`ConsoleLogFormatTests`:

```csharp
[TestCase(null, "Development", false)]
[TestCase(null, "Production", true)]
[TestCase(null, "Staging", true)]
[TestCase("Text", "Production", false)]
[TestCase("Json", "Development", true)]
[TestCase("json", "Development", true)]      // Review Focus 4
[TestCase("TEXT", "Production", false)]
[TestCase("Xml", "Production", true)]        // LoggingOptions rejects it; until then, the environment's default
[TestCase("Xml", "Development", false)]
public void IsJson_ChoosesBySettingThenEnvironment(string? setting, string environment, bool expected)

[TestCase(new[] { "--environment", "Development" }, false)]
[TestCase(new[] { "--environment", "Production" }, true)]
[TestCase(new[] { "--environment", "Development", "--Scanner:Logging:Format", "Json" }, true)]
[TestCase(new[] { "--environment", "Production", "--Scanner:Logging:Format", "text" }, false)]
public void IsJsonBeforeTheHost_ReadsTheCommandLine(string[] args, bool expected)
```

`ProgramTests`, new:

```csharp
[Test]
public async Task RunAsync_InProduction_WritesEveryLineAsJson()
// ValidArgs, empty FakeScanQueue: LogLines.Read(output) has at least two lines; the first's Message starts
// "Scanner starting in Production" and Value(line, "QueueName") is "scan-nightly"

[Test]
public async Task RunAsync_InDevelopment_WritesText()
// ValidArgs plus --environment Development: output contains "INF] RepoScanner.Scanner.ScanOrchestrator: Scanner
// starting in Development" and no line starts with "{"

[Test]
public async Task RunAsync_WithTheFormatSetToText_WritesTextInProduction()
// ValidArgs plus --Scanner:Logging:Format Text: as above, "Scanner starting in Production"

[Test]
public async Task Main_WithAFormatThatIsNeither_ExitsTwoWithOneLine()
// ValidArgs plus --Scanner:Logging:Format Xml: exit 2; exactly one line whose Level is "Fatal"; it contains
// "Scanner:Logging:Format must be Text or Json"

[Test]
public async Task RunAsync_AFailureWithQuotesAndLineBreaks_StaysOneJsonLine()   // Review Focus 1
// a hosted service throwing InvalidOperationException("git said \"fatal: not found\"\nremote: gone"): every line
// parses (LogLines.Read does not fail); the Fatal line's Value "@x" contains "remote: gone"
```

`ProgramTests`, `DatabaseRegistrationTests` and `LocalModeTests.AssertExitTwoWithOneLine`: each `output.Split('\n').Count(line => line.Contains("FTL]", ...))` becomes `LogLines.Read(output).Count(line => LogLines.Level(line) == "Fatal")`. In `RunAsync_WhenAzureDevOpsRefusesTheToken_ExitsOneWithOneLine`, the text check becomes: the one Fatal line's `Message` starts `Azure DevOps refused the token, scanner stopped:` and its `Problem` starts `Azure DevOps refused the token in Scanner:AzureDevOps:Pat`. That assertion is the rejected-PAT alert's pin; its comment says so.

`QueueModeTests`: the text checks that JSON's quoted `@m` breaks become property checks: the poison line (`PoisonQueueName` is the lane's poison queue, `Reason` is `The message is not JSON`), the skipped line (`RepositoryName` `secrets-repo`, `ScanRunId` the running run), the abandoned line (`ScanRunId` the abandoned run, `Message` contains `was abandoned`). In `Issue8_NothingTheProgramPrints_ContainsThePat`, the PAT and its Base64 must also be absent from every `LogLines.Strings` of every line (Review Focus 2).

`LocalModeTests`:

```csharp
[Test]
public async Task Path_WhenNuGetOrgGoesDown_LogsTheRetriesButNotEachRequest()   // Review Focus 5
// the packages fixture with _nuGet.Fail(): no line whose SourceContext starts "System.Net.Http.HttpClient" or "Polly"
// has Level "Information"; at least one line whose SourceContext starts "Polly" has Level "Warning"
```

- [ ] **Step 4: Run them to verify they fail**

Run: `dotnet test RepoScanner.sln --filter "FullyQualifiedName~ConsoleLogFormatTests|FullyQualifiedName~ProgramTests|FullyQualifiedName~QueueModeTests|FullyQualifiedName~LocalModeTests|FullyQualifiedName~DatabaseRegistrationTests" -p:CollectCoverage=false`
Expected: FAIL: `ConsoleLogFormat` not defined, then `LogLines.Read` failing on text lines.

- [ ] **Step 5: Write `ConsoleLogFormat`, `LoggingOptions` and wire both loggers**

`ConsoleLogFormat` as in the spec's sketch, comparing with `StringComparison.OrdinalIgnoreCase`, plus `IsJsonBeforeTheHost`, which builds a `ConfigurationBuilder` from `AddEnvironmentVariables()` and `AddCommandLine(args)` only. In `Program`, a `private static LoggerConfiguration WriteToConsole(LoggerSinkConfiguration writeTo, bool json)`: `new RenderedCompactJsonFormatter()` when `json`, else today's `ConsoleOutputTemplate`. The start-up logger uses `IsJsonBeforeTheHost(args)`; the host's logger uses `IsJson(builder.Configuration["Scanner:Logging:Format"], builder.Environment.EnvironmentName)` and gets the two `MinimumLevel.Override` lines. Register `LoggingOptions` in every mode, beside `DatabaseOptions`.

- [ ] **Step 6: Run the tests to verify they pass**

Same filter. Expected: PASS. If `Path_WhenNuGetOrgGoesDown_LogsTheRetriesButNotEachRequest` finds no Polly Warning, stop and ask the user (Review Focus 5).

- [ ] **Step 7: ADR-028, the ADR README, the README row and the design**

`docs/adr/ADR-028-json-log-lines-on-stdout-outside-development.md` from the spec's ADR-028 section, with the layout of ADR-027 (`- **Status:** Accepted`, `- **Date:** 2026-10-02`, `- **Source:** Decided while implementing Issue 10 (#61), task #65`, then Context, Decision, Consequences). `docs/adr/README.md`: its row, and the next new ADR becomes ADR-029. `README.md` Configuration table, after `Scanner:Database:ConnectionString`: `` | `Scanner:Logging:Format` | | `Text` in `Development`, `Json` elsewhere | `Text` or `Json`, in any case: one JSON object per line for Log Analytics, or readable text ([ADR-028](docs/adr/ADR-028-json-log-lines-on-stdout-outside-development.md)) | ``. The design's decision 9 and `ConsoleLogFormat` sketch say any case.

- [ ] **Step 8: Check by hand**

Run `dotnet run --project src/RepoScanner.Scanner` with Azurite running and an empty queue. Expected: text lines as before. Then `dotnet run --project src/RepoScanner.Scanner -- --Scanner:Logging:Format Json`. Expected: JSON lines, each starting `{"@t":`.

- [ ] **Step 9: Full build and tests**

As in Task 1.

- [ ] **Step 10: Stop for review, then commit and set #65 to Done**

```bash
git add Directory.Packages.props src/RepoScanner.Scanner/RepoScanner.Scanner.csproj src/RepoScanner.Scanner/Program.cs src/RepoScanner.Scanner/Logging/ConsoleLogFormat.cs src/RepoScanner.Scanner/Configuration/LoggingOptions.cs tests/RepoScanner.Scanner.Tests/LogLines.cs tests/RepoScanner.Scanner.Tests/Logging/ConsoleLogFormatTests.cs tests/RepoScanner.Scanner.Tests/ProgramTests.cs tests/RepoScanner.Scanner.Tests/QueueModeTests.cs tests/RepoScanner.Scanner.Tests/Local/LocalModeTests.cs tests/RepoScanner.Scanner.Tests/Configuration/DatabaseRegistrationTests.cs docs/adr/ADR-028-json-log-lines-on-stdout-outside-development.md docs/adr/README.md README.md docs/design/2026-10-02-observability-and-run-summary.md
git commit -m "This commit writes one CLEF JSON object per log line outside Development, with RenderedCompactJsonFormatter from Serilog.Formatting.Compact 3.0.0, for both the start-up logger and the host's, so the rejected-PAT line is JSON too; Scanner:Logging:Format, Text or Json in any case, overrides the environment and any other value exits 2; HttpClient and Polly lines below Warning no longer reach the console, while retries still do; the tests that read console output parse the JSON, and the PAT leak check also searches every unescaped value; and ADR-028 records the decision #65"
```

---

### Task 5: Acceptance tests, the alert lines, the container check and the README (#100)

**Files:**
- Modify: `tests/RepoScanner.Scanner.Tests/QueueModeTests.cs`, `scripts/test-container.ps1`, `README.md`

**Interfaces:**
- Consumes: `LogLines` (Task 4); the scope (Task 1), per-scanner line (Task 2) and summary (Task 3) as JSON properties.

- [ ] **Step 1: Set #100 to Doing**

- [ ] **Step 2: Write the acceptance tests**

`QueueModeTests`: `Body` gets an optional `Guid? correlationId = null`.

```csharp
// Issue 10 criterion 1: one search on a correlation ID returns every line of its scan
[Test]
public async Task Issue10_EveryLineOfAScan_CarriesItsOwnCorrelationIdAndRepository()
// secrets-repo with correlation A, then ghost (GhostId) with correlation B: every line either has a CorrelationId or
// its Message starts "Scanner starting" or "Stopped taking messages"; lines with A have RepositoryName "secrets-repo"
// and lines with B "ghost"; every A line comes before every B line; each ID's last line starts "Scan run "

// Issue 10 criterion 2
[Test]
public async Task Issue10_EachScan_HasOneSummaryLine()
// the same two messages: exactly one "Scan run " line per ID. A's: Level "Information", ScanRunStatus "Completed",
// CommitSha == _commitId, ScannerStatuses.Secret, .Package and .CodePattern "Succeeded". B's: Level "Warning",
// ScanRunStatus "Failed", CommitSha null, ErrorMessage contains "git clone failed with exit code 128", ScannerStatuses {}

// The poison alert's pin: it searches for Warning lines with PoisonQueueName
[Test]
public async Task Issue10_ThePoisonLines_AreWarningsWithPoisonQueueName()
// a malformed message and a secrets-repo message with dequeueCount 4: two lines with PoisonQueueName
// "scan-nightly-poison", both Level "Warning" and Message starting "Message "; the second has RepositoryName "secrets-repo"
```

- [ ] **Step 3: Run them**

Run: `dotnet test RepoScanner.sln --filter "FullyQualifiedName~QueueModeTests" -p:CollectCoverage=false`
Expected: PASS: Tasks 1 to 4 already did the work. A failure here is a gap in one of them: fix it in place and say which task it belonged to.

- [ ] **Step 4: `test-container.ps1` check 12 reads the container's JSON**

Add `Invoke-Captured`, beside `Invoke-Shown`, with the same `'Continue'` rule: it shows each line the command prints and returns them. Check 12 runs `docker run --rm --env-file $EnvFile -e Scanner__Logging__Format=Json $image` through it (`-e` wins over `.env`, so a `.env` set to Text still proves JSON), then fails, naming the line number, if a non-empty line does not parse with `ConvertFrom-Json`, or if no line has this check's `CorrelationId` and a `ScanRunStatus` of `Completed` or `PartiallyCompleted`. The help block's check list says so. `$checkCount` stays 13.

- [ ] **Step 5: README**

- **A new section, Logs**, before Known limitations: what one scan logs, in order (the spec's Purpose list); the correlation ID and repository on every scan line, and where the ID comes from; text in Development, JSON elsewhere, and `Scanner__Logging__Format=Text`; reading a container's log in Docker Desktop (the spec's Reading logs locally); the Log Analytics searches and the two alert searches, from the spec's Searches section; that Issue 12 creates the alert rules.
- **Run in a container, Build and run:** `docker rm reposcanner-scan`, then `docker run --name reposcanner-scan --env-file .env reposcanner:v1`, in place of `--rm`, saying why; the `.env` line for text.
- **Check it end to end:** check 12 also checks the container prints JSON with the message's correlation ID.
- **Known limitations:** the spec's Known limitations.

- [ ] **Step 6: Run the container check**

With Docker Desktop, Azurite and SQL Server running, from the repository root in Windows PowerShell: `.\scripts\test-container.ps1`
Expected: 13 `PASS` lines and `All 13 checks passed`.

- [ ] **Step 7: Full build and tests**

As in Task 1.

- [ ] **Step 8: Stop for review, then commit and set #100 to Done**

```bash
git add tests/RepoScanner.Scanner.Tests/QueueModeTests.cs scripts/test-container.ps1 README.md
git commit -m "This commit adds Issue 10's acceptance tests through the whole program, reading its JSON output: every line of a scan carries its own correlation ID and repository, and each scan has exactly one summary line, Completed with its commit and every scanner's status, or Failed with the clone's error; pins the poison lines the alert searches for; has test-container.ps1 check that the container prints JSON with the message's correlation ID; and adds the README's Logs section, with Docker Desktop, the Log Analytics searches and the alert searches, and the named docker run #100"
```
