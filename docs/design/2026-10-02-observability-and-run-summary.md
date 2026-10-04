# Issue 10: Observability and run summary (design)

- **Status:** Draft, for review
- **Date:** 2026-10-02
- **Work item:** Issue 10, #61 (tasks #62 to #65 and #100)
- **Branch:** `feature/61-observability-and-run-summary`

## Purpose

When a nightly run misbehaves at 02:00, the log is the only witness. Today it is a poor one:

- Every log call is already structured: all 40 in `src` use a fixed message template with named `{Properties}`, and none uses `$"..."` (checked on 2026-10-02). But the console writes each line as plain text ([Program.cs:34](../../src/RepoScanner.Scanner/Program.cs)), so the names are lost before Log Analytics sees them, and a line can only be found by its words.
- Only three lines carry the correlation ID, typed into their message by hand ([ScanOrchestrator.cs](../../src/RepoScanner.Scanner/ScanOrchestrator.cs)). The clone, the scanners and the findings do not, so one scan's lines cannot be picked out of a run that scanned forty.
- A scan that fails has no summary line: the one at [RepositoryScanRunner.cs:156](../../src/RepoScanner.Scanner/RepositoryScanRunner.cs) is logged only when the run completes.
- In a demo on 2026-10-02 (two repositories, one container), 230 of the 283 lines were `System.Net.Http.HttpClient` and `Polly` lines from the nuget.org calls.

One execution scans every message on its queue, one after another (Issue 7 design, The loop), so one container's log holds many repositories. After this Issue, one scan logs:

1. `Message ... for Kudos Wall/victim-repo, correlation ..., attempt 1`
2. `Cloned Kudos Wall/victim-repo main at ...`
3. For each scanner: its own detail line, then one line with its status, time and finding count (#63).
4. One line per new, reopened and resolved finding.
5. One summary line, last, whatever the outcome (#64).

Every one of those lines carries the message's `CorrelationId`, `ProjectName` and `RepositoryName` (#62). Outside Development, each is one JSON object, so Log Analytics can filter on any field (#65).

## Success criteria

From Issue 10 (#61):

1. A single Log Analytics query on `correlationId` returns every line of a run. Locally: every line logged while a request is handled carries its `CorrelationId` in the JSON output, checked by a test through the whole program and by `scripts/test-container.ps1`. In Azure: once Issue 12 deploys the job.
2. The summary line is asserted in a test.

Agreed on 2026-10-02:

3. The searches for the poison alert and the rejected-PAT alert are in the README, and a test fails if either log line they rely on changes. Issue 12's pipeline creates the alert rules.
4. `System.Net.Http.HttpClient` and `Polly` lines below Warning no longer reach the console.
5. `dotnet run` still prints readable text.

## Decisions

Agreed in the design discussion on 2026-10-02.

| # | Question | Decision | Reason |
|---|---|---|---|
| 1 | Where the correlation ID comes from | Unchanged: the message. The enqueuer makes one per message, and the dashboard one per "Scan now" click. `--path` makes its own ([LocalScanRunner.cs](../../src/RepoScanner.Scanner/Local/LocalScanRunner.cs)) | The sender can then find its scan run by it, as `test-container.ps1` does (Issue 9). It is saved as `ScanRuns.CorrelationId` |
| 2 | How it reaches every line (#62) | An `ILogger` scope holding `CorrelationId`, `ProjectName` and `RepositoryName`, opened in `ScanOrchestrator.HandleAsync` once the message is read, and in `RepositoryScanRunner.RunAsync` | A scope flows down the async calls to every logger, so the clone, git, the scanners and the finding lines get it without being changed. Serilog turns each scope pair into a property. Serilog's own `LogContext` would work too, but `FakeLogger` cannot see it, while it records scopes in `FakeLogRecord.Scopes`, so the unit tests can check them. `RunAsync` opens it too, so `--path` scans carry it. The repository's names are in it as well (agreed 2026-10-02), so one repository's lines can be found without first finding its correlation ID |
| 3 | Lines with no correlation ID or repository | Those logged before a message is read: start-up, `Scanner starting`, a malformed message, `Stopped taking messages`, and `Program`'s fatal lines | There is no request yet, or no longer one. In Azure, `ContainerGroupName` ties them to their execution |
| 4 | The per-scanner line (#63) | One line per scanner, from `RepositoryScanRunner` after it runs: `{ScannerType} scanner {ScannerStatus} in {ScannerDurationMs} ms: {FindingCount} findings`. Information when it succeeded; Warning with the exception when it failed, replacing `The {ScannerType} scanner failed; the other scanners carry on`. The scanners' own detail lines stay | The same shape for all three scanners, so one search compares them. The time and count are already measured for `ScannerResults` |
| 5 | The summary line (#64) | One per scan run, for every outcome: Completed, PartiallyCompleted and Failed, including a failed clone, the time limit, every scanner failing and the job being stopped. Logged last, once the outcome is saved or the save has failed. Information when Completed, Warning otherwise, with the error at the end. It replaces today's summary line and the `partially completed` warning | Today a failed scan has no summary, and the failures are what someone reads the log for |
| 6 | What the summary holds | `ScanRunId`, `ScanRunStatus`, `Trigger`, `ProjectName`, `RepositoryName`, `CommitSha` (null when the clone failed, and for `--path`), `DurationMs` (from the scan run's start, so the clone counts, until its outcome is saved, so deleting the clone afterwards is left out and it matches `ScanRuns`' times; amended in the Issue 10 review), `FoundCount`, `NewCount`, `ReopenedCount`, `ResolvedCount`, `SuppressionExpiredCount`, `ScannerStatuses` (an object such as `{"Package":"Failed","CodePattern":"Succeeded"}`, empty when no scanner ran), and `ErrorMessage` when not Completed | Task #64's list (repository, commit, new, reopened, resolved, duration, scanner statuses) with what a reader needs to act: which run, why it failed, nightly or on demand |
| 7 | Property names | One name means one thing everywhere. `{Status}` becomes `{ScanRunStatus}`; scanners use `{ScannerStatus}`. `DurationMs` is only a whole scan run's time: the clone line's becomes `{CloneDurationMs}`, the scanner line uses `{ScannerDurationMs}` | Otherwise `ScanRunStatus == "Failed"` or `DurationMs > 600000` would mix scan runs, scanners and clones. The rendered text does not change |
| 8 | The JSON layout (#65) | Serilog's compact JSON ([CLEF](https://clef-json.org)), written by `RenderedCompactJsonFormatter` from `Serilog.Formatting.Compact` 3.0.0: `@t`, `@m` (the finished message), `@i`, `@l`, `@x`, then every property at the top level | Serilog 4.3.0 has `JsonFormatter` built in, but its own documentation says new code should prefer the compact formatters or `Serilog.Expressions`. The rendered form keeps the sentence, so a person reading Docker Desktop or Log Analytics sees it; top-level properties keep the searches short (`e.CorrelationId`). `Serilog.Expressions` gives full control over the shape, with a template to maintain. 3.0.0 needs Serilog 4.0 or later; the scanner has 4.3.0 |
| 9 | When the console writes JSON | A new setting, `Scanner:Logging:Format`: `Text` or `Json`, in any case (amended in #65: .NET settings usually ignore case). When not set, `Text` in Development and `Json` in every other environment. Any other value exits 2, like every setting | Azure and the container run as Production (Issue 9 design, decision 4), so they get JSON with no setting. `dotnet run` uses Development (`launchSettings.json`), so it stays readable. A line `Scanner__Logging__Format=Text` in `.env` makes the container print text, as the README already suggests a `.env` line for the visibility timeout |
| 10 | The two loggers | The start-up logger (`Log.Logger`, [Program.cs:48](../../src/RepoScanner.Scanner/Program.cs)) and the host's logger follow the same rule, through one class. The start-up logger exists before configuration is read, so it takes `Scanner:Logging:Format` and the environment (`DOTNET_ENVIRONMENT`, or `--environment`) from environment variables and the command line only | `Azure DevOps refused the token, scanner stopped`, which the PAT alert searches for, is written by the start-up logger. If only the host's logger changed, that line would stay text |
| 11 | Noise | `MinimumLevel.Override` to Warning for `System.Net.Http.HttpClient` and `Polly`, as `Microsoft` already is | 230 of 283 lines in the demo. A retry still shows: Polly logs it as a warning (checked in #65) |
| 12 | Alerts | This Issue writes the two searches into the README and pins the lines they rely on with tests. Issue 12's pipeline creates the alert rules (recorded on #61 and #72) | Nothing is applied to Azure by hand (ADR-006), so everything in this Issue can be finished locally |
| 13 | Which Log Analytics table | `ContainerAppConsoleLogs`, with the line in its `Log` column | It is filled by the Azure Monitor logs destination. The older `ContainerAppConsoleLogs_CL` table is filled through an API Microsoft is retiring ([Microsoft Learn](https://learn.microsoft.com/azure/container-apps/migrate-logs-azure-monitor)). Issue 12 sets the Container Apps environment's logs to Azure Monitor |
| 14 | Reading logs locally | Docker Desktop. The README's `docker run` gets `--name reposcanner-scan` and loses `--rm`, with `docker rm reposcanner-scan` first, so the last run's container and its log stay. No Kusto emulator and no Seq | Docker Desktop's log search finds a correlation ID. The Kusto emulator runs the same query language but is several GB and needs the lines loaded by hand; Seq has its own query language, so neither proves the Azure searches. Those are proved in Issue 12 |
| 15 | One container, many repositories | Unchanged (Issue 7 design). ADR-012 is amended: its window "N × (scan time + 30 s poll + cold start)" counts a cold start per repository, but there is one per execution | See ADR-012 amendment |
| 16 | The acceptance tests | Through the whole program, as `QueueModeTests` already runs it, reading the JSON it prints. `test-container.ps1` check 12 also reads the container's JSON | The tests run as Production, so they print what Azure will get |
| 17 | The tests that read console output | `ProgramTests`, `QueueModeTests`, `LocalModeTests` and `DatabaseRegistrationTests` read JSON lines through a small helper, `LogLines`, instead of looking for text such as `FTL]` | They run as Production, so after #65 they get JSON |
| 18 | New packages | `Serilog.Formatting.Compact` 3.0.0 | Decision 8 |
| 19 | New ADRs | ADR-028, JSON log lines on stdout outside Development (#65). ADR-012 amended (#61) | See ADR-028 and ADR-012 amendment |
| 20 | A fifth task | #100: the acceptance tests, the pinned alert lines, `test-container.ps1` reading JSON, and the README's Logs section. Added under #61 on 2026-10-02 | Like #41 in Issue 8: the proof and the documentation, once every piece is in |
| 21 | Where this document lives | `docs/design/` | Same as Issues 2 to 9 |

## Project layout

```
src/RepoScanner.Scanner/
  Logging/ScanLogScope.cs                the scope: CorrelationId and the names   #62
  Logging/ConsoleLogFormat.cs            Text or Json, for both loggers           #65
  Configuration/LoggingOptions.cs        Scanner:Logging:Format                   #65
  ScanOrchestrator.cs                    opens the scope per message              #62
  RepositoryScanRunner.cs                the scope, the per-scanner line and
                                         the summary line                         #62, #63, #64
  Sources/AzureDevOpsCloneSource.cs      {CloneDurationMs}                        #63
  Program.cs                             JSON or text, the noise overrides        #65
  RepoScanner.Scanner.csproj             Serilog.Formatting.Compact               #65

Directory.Packages.props                 Serilog.Formatting.Compact 3.0.0         #65

tests/RepoScanner.Scanner.Tests/
  LogLines.cs                            reads the JSON lines a test captured     #65
  Logging/ConsoleLogFormatTests.cs                                                #65
  RepositoryScanRunnerTests.cs                                                    #62, #63, #64
  ScanOrchestratorTests.cs                                                        #62
  ProgramTests.cs                                                                 #65
  QueueModeTests.cs                      JSON in #65; Issue 10 acceptance tests   #65, #100
  Local/LocalModeTests.cs                                                         #65
  Configuration/DatabaseRegistrationTests.cs                                      #65

scripts/test-container.ps1               check 12 reads the container's JSON      #100

docs/adr/ADR-012-maxexecutions-1-raised-on-evidence.md     amended               #61
docs/adr/ADR-028-json-log-lines-on-stdout-outside-development.md                 #65
docs/adr/README.md                       ADR-012 amended, ADR-028 added           #61, #65
README.md                                Scanner:Logging:Format in #65; the rest
                                         in #100                                  #65, #100
```

No database change and no change to the message format.

## The correlation ID and the repository (#62)

```csharp
namespace RepoScanner.Scanner.Logging
{
    // The logging scope that puts a scan's correlation ID and repository on every line logged inside it, by any
    // class (#62). Serilog turns each pair into a property of the event; FakeLogger keeps it in FakeLogRecord.Scopes.
    public static class ScanLogScope
    {
        public static IReadOnlyDictionary<string, object> For(Guid correlationId, string projectName, string repositoryName)
        {
            return new Dictionary<string, object>
            {
                ["CorrelationId"] = correlationId,
                ["ProjectName"] = projectName,
                ["RepositoryName"] = repositoryName
            };
        }
    }
}
```

- `ScanOrchestrator.HandleAsync` opens it with `_logger.BeginScope(ScanLogScope.For(request.CorrelationId, request.ProjectName, request.RepositoryName))` as soon as `ScanRequestReader.TryRead` succeeds, and keeps it open until the message is deleted. Its own lines for that request (skipped, poisoned after three attempts, abandoned run taken over, `Message ...`, failed) get it, and so does every line from the scan.
- `RepositoryScanRunner.RunAsync` opens the same scope, from its `RepositoryDetails`, around its whole body. Inside the orchestrator's scope it adds nothing new; for `--path` it is the only one, with the project `Local` and the folder's name.
- The lines that already write `correlation {CorrelationId}` or `{ProjectName}/{RepositoryName}` in their text keep them. The event's own property and the scope's have the same value, and mean the same thing (decision 7).

## The per-scanner line (#63)

Logged by `RepositoryScanRunner` as each scanner finishes, from the `ScannerResult` it already builds:

```
Secret scanner Succeeded in 41 ms: 6 findings
Package scanner Failed in 30012 ms: 0 findings      (Warning, with the exception)
```

A scanner stopped by the time limit or the job logs no line: the run's summary says it was stopped. The clone line's `{DurationMs}` becomes `{CloneDurationMs}` (decision 7).

## The summary line (#64)

Logged once per scan run, after its outcome is saved. As text, on one line:

```
Scan run 1 Completed (Nightly) for Kudos Wall/victim-repo at b65334233db66c3f774550c27d47bed3421102c8 in 2748 ms: 14 found, 14 new, 0 reopened, 0 resolved, 0 suppressions expired. Scanners: {"Secret":...,"Package":"Succeeded","CodePattern":"Succeeded"}
```

When not Completed, the same line at Warning, ending `. Error: {ErrorMessage}`, for example `Error: git clone failed with exit code 128 ...`. The template is otherwise the same, so the same properties are on both.

As JSON, the same event on one line, shown here across several:

```json
{"@t":"2026-10-02T10:59:59.4410000Z",
 "@m":"Scan run 1 Completed (Nightly) for \"Kudos Wall\"/\"victim-repo\" at \"b653...\" in 2748 ms: ...",
 "@i":"5f1c2a7e",
 "ScanRunId":1,"ScanRunStatus":"Completed","Trigger":"Nightly",
 "ProjectName":"Kudos Wall","RepositoryName":"victim-repo","CommitSha":"b65334233db66c3f774550c27d47bed3421102c8",
 "DurationMs":2748,"FoundCount":14,"NewCount":14,"ReopenedCount":0,"ResolvedCount":0,"SuppressionExpiredCount":0,
 "ScannerStatuses":{"Secret":...,"Package":"Succeeded","CodePattern":"Succeeded"},
 "SourceContext":"RepoScanner.Scanner.RepositoryScanRunner",
 "CorrelationId":"77438d01-1e66-47ae-a0ed-bd5c86c6f446"}
```

- `@l` is missing because the line is Information: CLEF leaves it out for that level. A Warning has `"@l":"Warning"`.
- `@m` puts quotes round string values, which is how Serilog renders a message unless the template says otherwise. The text console's `{Message:lj}` does not.
- The secret scanner's status is shortened to `...` in this document's examples. RepoScanner's own SEC007 rule reads a quoted name containing "secret" followed by a value of 8 or more characters as a named secret, so the self-scan (`SelfScanTests`) would report `Succeeded` there. The real line has it in full, and the README's examples follow the same rule.
- In the failure path, `RecordFailureAsync` logs the summary with the scanners' results so far, which means the list of results moves up to the start of `RunAsync`.

## JSON or text (#65)

```csharp
namespace RepoScanner.Scanner.Logging
{
    // Text for a person at a terminal, JSON for Log Analytics (#65, ADR-028). Both the start-up logger and the
    // host's logger ask this class, so the two never differ.
    public static class ConsoleLogFormat
    {
        // setting is Scanner:Logging:Format, or null when not set. Text or Json in any case wins, as .NET settings
        // usually ignore case. A value that is neither is left to LoggingOptions to reject (exit 2), and the
        // environment's default is used until then.
        public static bool IsJson(string? setting, string environmentName)
        {
            if (string.Equals(setting, "Json", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(setting, "Text", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return !string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase);
        }
    }
}
```

- **The host's logger** (`CreateBuilder`): `builder.Configuration["Scanner:Logging:Format"]` and `builder.Environment.EnvironmentName`. With JSON, `.WriteTo.Console(new RenderedCompactJsonFormatter())`; with text, today's `outputTemplate`.
- **The start-up logger** (`RunAsync`): a small configuration from environment variables and the command line, read before the host is built: `Scanner:Logging:Format`, then the environment from `--environment`, else `DOTNET_ENVIRONMENT`, else Production.
- **`LoggingOptions`**: `Format`, empty or `Text` or `Json` in any case (`[RegularExpression("^(?i:text|json)$")]`), bound and validated on start like the other groups, so `Scanner__Logging__Format=Xml` exits 2 with one line.
- **The noise overrides**: `.MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)` and `.MinimumLevel.Override("Polly", LogEventLevel.Warning)`, next to `Microsoft`.
- Container Apps keeps one row per line, so an exception's stack trace, which today spreads over many rows, stays in one row in `@x`.

## Searches

Each line is in the `Log` column of `ContainerAppConsoleLogs` as text, so a search first unpacks it with `parse_json`. These go in the README's Logs section (#100).

**One scan, every line:**

```kusto
ContainerAppConsoleLogs
| extend e = parse_json(Log)
| where tostring(e.CorrelationId) == "77438d01-1e66-47ae-a0ed-bd5c86c6f446"
| project Time = todatetime(e["@t"]), Level = coalesce(tostring(e["@l"]), "Information"), Message = tostring(e["@m"])
| order by Time asc
```

**Scans that did not complete, in the last day:**

```kusto
ContainerAppConsoleLogs
| where TimeGenerated > ago(1d)
| extend e = parse_json(Log)
| where isnotempty(tostring(e.ScanRunStatus)) and tostring(e.ScanRunStatus) != "Completed"
| project TimeGenerated, Repository = tostring(e.RepositoryName), Status = tostring(e.ScanRunStatus), Error = tostring(e.ErrorMessage), CorrelationId = tostring(e.CorrelationId)
```

**One repository's lines in the last day**: `where TimeGenerated > ago(1d) and tostring(e.RepositoryName) == "Kudos-Wall"`. **Slow scans**: `where toint(e.DurationMs) > 600000`. **Each scanner's average time**: `summarize avg(toint(e.ScannerDurationMs)) by tostring(e.ScannerType)`.

### The alert searches

**A message moved to a poison queue.** Only the two poison lines carry `PoisonQueueName`, so the search does not depend on their wording:

```kusto
ContainerAppConsoleLogs
| extend e = parse_json(Log)
| where tostring(e["@l"]) == "Warning" and isnotempty(tostring(e.PoisonQueueName))
| project TimeGenerated, ContainerGroupName, Message = tostring(e["@m"])
```

**A rejected PAT.** The start-up logger's fatal line:

```kusto
ContainerAppConsoleLogs
| extend e = parse_json(Log)
| where tostring(e["@l"]) == "Fatal" and tostring(e["@m"]) startswith "Azure DevOps refused the token"
| project TimeGenerated, ContainerGroupName, Message = tostring(e["@m"])
```

Tests pin what these rely on: both poison lines are Warning with `PoisonQueueName`, and the rejected-PAT line is Fatal and starts `Azure DevOps refused the token`. How often the alert runs, and who it tells, is Issue 12's.

## Reading logs locally

```bash
docker rm reposcanner-scan
```

```bash
docker run --name reposcanner-scan --env-file .env reposcanner:v1
```

- The first command removes the last run's container, so the name is free. The first time, Docker says there is no such container, which is harmless.
- The stopped container stays in Docker Desktop, under Containers, with its log. The log's search box finds a correlation ID.
- One container scans every message on the queue, then exits. A second `docker run` is a new container.
- `Scanner__Logging__Format=Text` in `.env` makes the container print text.

## ADR-028 (#65)

**ADR-028: JSON log lines on stdout outside Development.** *Status: Accepted. Date: 2026-10-02. Source: decided while implementing Issue 10 (#61).*

- **Context:** Container Apps sends each line the scanner writes to stdout to Log Analytics as one text value. The scanner's log calls name every value (`{CorrelationId}`, `{ScanRunStatus}`), but the console's text layout turns each line into a sentence, so a search can only match words, and an alert depends on exact wording. Two loggers write to the console: the host's, and the start-up logger that reports why the scanner could not start or was stopped.
- **Decision:** outside Development, both loggers write one CLEF JSON object per line with `RenderedCompactJsonFormatter` from `Serilog.Formatting.Compact`: the time, the finished message, the level, any exception, and every property at the top level, including `SourceContext`, and the `CorrelationId`, `ProjectName` and `RepositoryName` from the scan's logging scope. Development writes text. `Scanner:Logging:Format` (`Text` or `Json`) overrides the environment.
- **Consequences:** a search unpacks a line with `parse_json(Log)` and filters on any property. An exception and its stack trace stay in one row. Property names become part of the searches and alerts, so renaming one breaks them: tests pin the lines the alerts rely on. Raw JSON is harder to read than text, which is why Development keeps text. CLEF leaves out the level for Information lines, and the rendered message quotes string values. One more package to keep up to date.

## ADR-012 amendment (#61)

**2026-10-02:** One execution takes messages one at a time until its queue is empty or too little of `MaxRunDurationMinutes` is left to start another repository (Issue 7 design, The loop), so the cold start and the 30-second poll are paid once per execution, not once per repository. At 6 minutes a repository, about 6 fit in a 50-minute execution, since one is only started while 15 minutes are left, so 40 repositories take about 7 executions: still about four hours from 02:00, plus 7 cold starts rather than 40. The decision is otherwise unchanged.

## README

- **Configuration:** a row for `Scanner:Logging:Format` (#65).
- **A new section, Logs:** what one scan logs, in order; the correlation ID and where it comes from; text in Development and JSON elsewhere; reading a container's log in Docker Desktop; the searches and the two alert searches above; that the alert rules are created in Issue 12 (#100).
- **Run in a container, Build and run:** the named `docker run` with `docker rm` first, in place of `--rm`, and the `.env` line for text (#100).
- **Check it end to end:** check 12 also reads the container's JSON (#100).
- **Known limitations:** the limits below (#100).

## Testing

`dotnet test` still needs no Azurite, SQL Server or Docker.

| Task | Checks |
|---|---|
| #62 | `RepositoryScanRunnerTests`: every record the runner logs during a scan has the scope with `CorrelationId`, `ProjectName` and `RepositoryName`, for a completed and a failed scan, and for `--path`. `ScanOrchestratorTests`: each line about a read message has its own message's ID and repository, for a scanned, a skipped and a poisoned-after-three-attempts message; the malformed-message line has no scope |
| #63 | One line per scanner with `ScannerType`, `ScannerStatus`, `ScannerDurationMs` and `FindingCount`; a failed scanner's is Warning with the exception, and the old warning is gone; the clone line has `CloneDurationMs` |
| #64 | Exactly one summary line per scan run, logged last, for Completed, PartiallyCompleted, every scanner failing, a failed clone and the time limit, each with the properties in decision 6; Warning with `ErrorMessage` unless Completed; no `{Status}` property left |
| #65 | `ConsoleLogFormatTests`: Development gives text, Production and any other environment JSON, and the setting overrides both ways. `ProgramTests`: in Production every line is JSON, including the start-up logger's fatal line; with `--environment Development`, text; `--Scanner:Logging:Format Text` in Production, text; `Xml` exits 2 with one line. `QueueModeTests`: no `System.Net.Http.HttpClient` or `Polly` line below Warning. The console-reading tests use `LogLines` (decision 17) |
| #100 | `QueueModeTests`, with two messages (one that scans, one whose clone fails): every line is either one of decision 3's or carries a `CorrelationId` and `RepositoryName`, and each message's lines, from `Message ...` to its summary, carry its own; each message has exactly one summary line, with a 40-character `CommitSha` for the scan and `Failed` with an `ErrorMessage` for the failed clone. The two alert lines are pinned as above. `test-container.ps1` check 12: every line the container prints is JSON, and its summary line has the message's correlation ID |

## Order of work

One task at a time, stopping after each for review, then a commit whose message starts "This commit" and ends in its task number (ADR-020):

1. **#61** This design document and the ADR-012 amendment.
2. **#62** `ScanLogScope`, opened in `ScanOrchestrator` and `RepositoryScanRunner`.
3. **#63** The per-scanner line and `CloneDurationMs`.
4. **#64** The summary line for every outcome.
5. **#65** JSON or text, `LoggingOptions`, the noise overrides, the console-reading tests on `LogLines`, the package, ADR-028 and the README's Configuration row.
6. **#100** The acceptance tests, `test-container.ps1` check 12, and the README's Logs section and container commands.

## Not in Issue 10

| Item | Where it is on the board |
|---|---|
| The alert rules, how often they run and who they tell | Issue 12 (#72), using the searches here |
| Setting the Container Apps environment's logs to Azure Monitor | Issue 12 |
| The enqueuer's log lines and its correlation ID | Enqueuer Issue 4 (#82) |
| The dashboard's log lines | Dashboard Issue 1 (#92) |
| A local Log Analytics, Kusto emulator or Seq | Not on the board (decision 14) |
| Walking a repository's files once instead of once per scanner | Not on the board (see Known limitations) |
| Traces and metrics (Application Insights) | Not on the board |

## Known limitations

- Lines logged before a message is read, or after the loop stops, have no correlation ID or repository (decision 3).
- The file walker logs its `Found ... files` line once per scanner, so three times per scan, because each scanner walks the files itself. Left as it is (agreed 2026-10-02): each line still says what that walk skipped.
- Text lines, in Development or with `Scanner__Logging__Format=Text`, can only be searched by their words.
- The start-up logger reads `Scanner:Logging:Format` from environment variables and the command line only, not from appsettings files.
- In JSON, Information lines have no `@l`, and `@m` quotes string values.
- The searches are checked against Log Analytics only once Issue 12 deploys the job; until then, tests check the JSON lines they rely on.
- Property names are now part of the searches: renaming one breaks any search that uses it.
