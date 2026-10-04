# Issue 11: Test hardening and coverage gate (design)

- **Status:** Draft, for review
- **Date:** 2026-10-04
- **Work item:** Issue 11, #66 (tasks #120, #101, #68, #69 and #67)
- **Branch:** `feature/66-test-hardening-and-coverage-gate`

## Purpose

The proposal promises 80% test coverage, and the fixtures were built to be a regression suite. This Issue checks that both promises hold: the tests catch a rule that stops working, odd repositories scan cleanly, and coverage stays at 80% or more on Core and Scanner.

Coverage is already well past the target. On 2026-10-02, `dotnet test` passed 870 tests (1 skipped) with:

| Project | Line | Branch | Method | Gate |
|---|---|---|---|---|
| Core | 100% | 100% | 100% | 80% line, enforced |
| Scanner | 98.66% | 95.55% | 100% | 80% line, enforced |
| Data | 100% | 100% | 100% | Reported only |

So the work is not raising a number. It is the five things the number does not show:

1. Whether a test fails when a rule is broken (#69).
2. Whether odd repositories, an empty one or one with a single huge line, scan cleanly (#68).
3. The real logic among the 30 Scanner lines no test runs: an unreadable project file, two `SnippetHash` naming cases, and a `Format(...)` call CP001 misses (#68, #69).
4. One example of the scan request that the enqueuer and the dashboard copy, read by a scanner test (#101).
5. A record that this release has no CI/CD pipeline, so every check here runs locally (#120).

## Success criteria

From Issue 11 (#66), as amended on 2026-10-04:

1. The coverage report is in the pull request: the Core, Scanner and Data table in its description.
2. Deliberately breaking a rule makes `dotnet test` fail locally, with the failing output in the pull request.

Agreed on 2026-10-04:

3. CP001 flags SQL built with a bare `Format(...)` or `Concat(...)` when the file has `using static System.String;`.
4. The example scan request in `docs/contracts` is read and accepted by a scanner test.
5. ADR-029 records that this release runs locally only, with no CI/CD pipelines.

## Decisions

Agreed in the design discussion on 2026-10-04, and on 2026-10-02 where marked.

| # | Question | Decision | Reason |
|---|---|---|---|
| 1 | CI | None in this release. The coverage gate and the mutation check run locally with `dotnet test` (ADR-029) | The enqueuer and the dashboard are built and run locally first. Scanner Issue 12 (#72), enqueuer Issue 6 (#84) and dashboard Issue 3 (#99) are out of scope, kept at the bottom of the board |
| 2 | Order of tasks | #120, #101, #68, #69, #67 | ADR-029 first, since the Issue's wording depends on it; #101 before enqueuer Issue 3 (#81) and the dashboard's Scan now (#88) need it; the coverage table last, once every test is in |
| 3 | An empty Azure DevOps repository | The enqueuer leaves out a repository with no default branch, which is what Azure DevOps gives an empty one (enqueuer Issue 2, #80). The scanner keeps Issue 8's behaviour as a safety net: a clean failed clone with git's reason and no folder left (`OpenAsync_ForAnEmptyRepository_FailsWithGitsReasonAndLeavesNoFolder`). Issue 11 tests an empty folder through `--path` | A queued empty repository fails its clone, its message is tried again, and after the third attempt it goes to the poison queue ([ScanOrchestrator.cs](../../src/RepoScanner.Scanner/ScanOrchestrator.cs)). Every empty repository would do that every night. Left out at the source, it never reaches the queue |
| 4 | A repository with no `.csproj` | No new test: it is already tested end to end. `Path_OnTheFixture_CompletesTheRunWithASucceededResultForEachScanner` runs the whole program on the secrets fixture, which has no project files, and checks the package scanner succeeds with zero findings and asks nuget.org nothing. `PackageScannerTests.ScanAsync_AFolderWithNoProjectFiles_MakesNoRequest` checks the same in the scanner alone | Found while reading the tests for this design. A second test would check the same thing |
| 5 | A huge line | One file holding a single line of exactly 1,048,576 bytes, the largest file the walker scans, with a SEC003 connection string at its very end. The password is found on line 1, the scan run is Completed, and no rule logs `timed out` | Each secret pattern stops after 1 second ([SecretRulePattern.cs](../../src/RepoScanner.Scanner/Secrets/SecretRulePattern.cs)). A pattern that times out is skipped for that file only, with a warning, and the scan carries on (Issue 3 design), so a secret at the end of a minified file would be missed quietly. If this test finds a pattern that times out, fixing that pattern is part of #68 |
| 6 | The uncovered real logic | Unit tests for an unreadable `.csproj` ([ProjectFileReader.cs:60-62](../../src/RepoScanner.Scanner/Packages/ProjectFileReader.cs)) and the two `SnippetHash` naming cases ([SnippetHash.cs:56 and 68](../../src/RepoScanner.Scanner/CodePatterns/SnippetHash.cs)) | From the coverage check on 2026-10-02 (#68). The other uncovered lines are safety nets for failures inside failures, and are left (Known limitations) |
| 7 | CP001 and a bare `Format(...)` | Flagged when the file, or a namespace holding the call, has `using static System.String;` (or `using static String;`). A bare `Format(...)` or `Concat(...)` without it is someone else's method and is not flagged | Today the rule needs `string.Format` or `String.Format` written out ([Cp001SqlFromStringsRule.cs:66-68](../../src/RepoScanner.Scanner/CodePatterns/Rules/Cp001SqlFromStringsRule.cs)), so SQL built after a `using static` is missed. Checking for the `using` keeps a project's own `Format` method from raising a false alarm |
| 8 | The fixture | One bare `Format(...)` planted in a new file in `tests/fixtures/patterns-repo`, added to `FixturePatterns.All` and the fixture's README | Issue 11's reason is to make the fixtures a regression suite, so the whole-program fixture test catches the case too |
| 9 | The mutation check (#69) | By hand, on SEC003 and CP001: break each rule, run the tests, record which fail, put the rule back with `git checkout`, run them again. The change, the failing tests and the green run go in the pull request's "How it was tested". Nothing about it is committed | One rule from each scanner that reads code. SEC003 has the most involved pattern; CP001 is the rule this Issue changes. A mutation tool such as Stryker.NET would be thorough, but slow over 870 tests and a new dependency, for a check the task asks to do once |
| 10 | The coverage report (#67) | The Core, Scanner and Data table from `dotnet test --tl:off`, pasted into the pull request's "How it was tested". The gate is unchanged: 80% line coverage on Core and Scanner, Data reported only | The reports are git-ignored XML files. A table is what a reviewer reads; ReportGenerator would add a tool for the same numbers |
| 11 | The example scan request (#101) | `docs/contracts/scan-request.example.json`: a valid version 1 message. A test in `ScanRequestReaderTests` reads the file and checks `TryRead` accepts it with each field as written | The scanner owns the message format (Issue 7 design, decision 1). Today the tests build their messages in code, so nothing checks the file the other two repositories copy |
| 12 | Separate repositories for the enqueuer and the dashboard | Kept, recorded in ADR-029 (said by the user on 2026-10-04) | Each app keeps its own code and history. The message format is the only thing the enqueuer shares, and #101 gives it one file to copy. The rest of each repository's setup stays with #102 and #93 |
| 13 | Which ADRs ADR-029 amends | ADR-005, ADR-006, ADR-007, ADR-008 and ADR-020. ADR-008 is added to #120's list | ADR-008's decision names the pipeline identity as the one holding DDL rights. Locally, `dotnet ef` applies migrations with the developer's own Windows login, and the container's `repo_scanner_local` login has only `db_datareader` and `db_datawriter` (README, Database and migrations), so the two levels still hold |

## ADR-029 (#120)

A new ADR, `docs/adr/ADR-029-this-release-runs-locally-without-pipelines.md`, in the usual Context, Decision and Consequences form.

- **Context:** ADR-017 has everything run locally before Azure. The enqueuer and the dashboard are not built yet, and a pipeline built now would deploy apps that have never run together.
- **Decision:** this release runs locally only, on Azurite, SQL Server Express and Docker Desktop, with no CI/CD pipeline and no Azure resources. The three pipeline Issues are kept on the board, out of scope, so they can come back if the EPA needs evidence of an automated build or deployment. The enqueuer and the dashboard keep their own repositories (decision 12).
- **Consequences:** tests run by hand before each merge. The ADRs that describe Azure (ADR-001, ADR-003, ADR-009, ADR-011, ADR-014, ADR-015 and ADR-023) describe the target and are unchanged. Five ADRs get a dated amendment:

| ADR | Today | Amendment |
|---|---|---|
| ADR-005 Immutable image tags | Tagged with `$(Build.BuildId)` | No build ID locally: the image is tagged by hand, `reposcanner:v1` (Issue 9) |
| ADR-006 Migrations applied by the pipeline | A Migrate stage before deploying | Locally, `dotnet ef database update`, as the README's Database and migrations section shows |
| ADR-007 Managed identity throughout | The pipeline signs in with Workload Identity Federation | No pipeline, so nothing signs in. The rest stands |
| ADR-008 Two privilege levels | The pipeline identity holds DDL rights | Locally, the developer's own login applies migrations; the scanner's login reads and writes only |
| ADR-020 Branching strategy and branch policies | A build validation policy once a pipeline exists | No build validation policy: run `dotnet test` before completing a pull request. The other policies stand |

Also in #120:

- The ADR README's table gets ADR-029, the amended rows say so, and the next new ADR becomes ADR-030.
- The README's three pipeline mentions (Database and migrations, the alert rules, and the Log Analytics known limitation) say the pipeline is not in this release (ADR-029). Earlier design documents are dated records and are left as written.
- The board: #66, #67 and #69 say "local only (ADR-029)" in place of "moved to Issue 12".

## Example scan request (#101)

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

- The values are the Issue 7 design's example, which `ScanRequestReaderTests.ValidFields` already copies, so there is one example message. That design's Message format section gets a line pointing to this file.
- `defaultBranch` is the branch's name, not `refs/heads/develop`, which the scanner rejects. The enqueuer strips the prefix Azure DevOps gives.
- The test, `TryRead_TheExampleInDocs_IsAccepted`, reads the file from the repository root, as the fixture tests find their folders.

## Edge cases (#68)

| Test | Where | Checks |
|---|---|---|
| `Path_OnAnEmptyFolder_CompletesWithNoFindings` | `LocalModeTests` | Exit 0; one Completed scan run with no findings; three scanner results, each Succeeded with zero findings; no request to nuget.org |
| `Path_OnAFileWithOneHugeLine_FindsTheSecretAtItsEnd` | `LocalModeTests` | A one-line file of exactly 1,048,576 bytes with a SEC003 connection string at the end: one SEC003 finding on line 1, the run Completed, and no `timed out` line in the log |
| `ReadAsync_AProjectFileThatCannotBeRead_IsSkippedAndTheOthersAreRead` | `ProjectFileReaderTests` | Two project files, one held open with `FileShare.None` while it is read: the other's packages are declared, and both count as project files found |
| `Compute_OnCodeOnTheTypeItself_UsesNoMemberName` | `SnippetHashTests` | Code in an attribute on a class is hashed with an empty member name |
| `Compute_InAFieldDeclarationOutsideEveryInitialiser_UsesTheFirstFieldsName` | `SnippetHashTests` | In `[Attr(...)] Action _first = null, _second = ...;`, code in the attribute is hashed with `_first` |

The folders are built in a temporary folder by each test (`TempFolder`), as the existing tests do.

## CP001 and the mutation check (#69)

**The fix.** In [Cp001SqlFromStringsRule.cs](../../src/RepoScanner.Scanner/CodePatterns/Rules/Cp001SqlFromStringsRule.cs), `IsStringFormatOrConcat` also accepts a call whose expression is a bare name, `Format` or `Concat`, when a `using static` directive naming `String` (`System.String` or `String`) is in the file or in a namespace declaration holding the call. The rule still reads one file at a time, with no semantic model (Issue 6 design).

```csharp
using static System.String;
...
db.Database.ExecuteSqlRaw(Format("SELECT * FROM Users WHERE Id = {0}", id)); // flagged after this Issue
```

**Tests** in `Cp001SqlFromStringsRuleTests`:

| Case | Expected |
|---|---|
| Bare `Format(...)` with `using static System.String;` at the top | Flagged |
| Bare `Concat(...)` with the same `using` | Flagged |
| The `using` inside a namespace block holding the call | Flagged |
| Bare `Format(...)` with no `using static` (the project's own method) | Not flagged |

The fixture gets a new file, `tests/fixtures/patterns-repo/Data/StaticFormatQueries.cs`, with one planted bare `Format(...)` (decision 8).

The Issue 6 design's CP001 section gets a dated note: the bare form is flagged after a `using static`, and the known limit below.

**The mutation check**, by hand, once the fix is in:

1. **SEC003:** change its pattern so a connection string's password no longer matches. Run the Scanner tests and note which fail.
2. **CP001:** make the `+` join case return "not built from strings". Run the tests and note which fail.
3. Put both back with `git checkout`, then run the tests again: all green.
4. Paste the two changes, the failing tests and the green run into the pull request.

If breaking a rule makes no test fail, that is a gap: the missing test is added in #69 before moving on.

## Coverage gate (#67)

No code change: [Directory.Build.props](../../Directory.Build.props) already fails `dotnet test` below 80% line coverage on Core and Scanner. Once #68 and #69 are in, `dotnet test RepoScanner.sln --tl:off` is run and its three coverage tables go into the pull request (decision 10).

## Testing

`dotnet test` still needs no Azurite, SQL Server or Docker.

| Task | Checks |
|---|---|
| #120 | None: documents only |
| #101 | `ScanRequestReaderTests`: the example file is accepted, with each field as written |
| #68 | The five tests in Edge cases |
| #69 | The four CP001 cases; `FixturePatternScanTests` and `LocalModeTests` with the planted fixture case; the mutation check by hand |
| #67 | `dotnet test` passes with Core and Scanner at 80% or more |

## Order of work

One task at a time, stopping after each for review, then a commit whose message starts "This commit" and ends in its task number (ADR-020):

1. **#66** This design document.
2. **#120** ADR-029, the five amendments, the ADR README, the README's three pipeline mentions, and the board wording.
3. **#101** The example scan request, its test, and the Issue 7 design's pointer.
4. **#68** The five edge-case tests, and any pattern fix the huge line needs.
5. **#69** The CP001 fix, its tests, the fixture case and the Issue 6 design note; then the mutation check by hand.
6. **#67** `dotnet test`, and the coverage table ready for the pull request.

## Not in Issue 11

| Item | Where it is on the board |
|---|---|
| Running the gate or the mutation check in CI | Out of scope with #72 (ADR-029) |
| Leaving empty repositories out of the queue | Enqueuer Issue 2 (#80), noted there |
| The enqueuer's and the dashboard's contract tests on the example file | Enqueuer Issue 3 (#81); the dashboard's Scan now (#88) |
| A mutation testing tool such as Stryker.NET | Not on the board (decision 9) |
| Raising the threshold, gating branch coverage, or enforcing Data | Not on the board |

## Known limitations

- CP001 does not see a `global using static System.String;` in another file, such as `GlobalUsings.cs`, or an alias such as `using S = System.String;`, because it reads one file at a time.
- The mutation check is done once. A later change that weakens a test is not caught by it.
- Nothing stops a pull request being completed without running `dotnet test` (ADR-020 amendment).
- The symbolic-link test skips on Windows without Developer Mode, so a local run does not cover the walker's link lines.
- Safety nets for failures inside failures stay uncovered: saving a Failed run when the database also fails ([RepositoryScanRunner.cs:256-259](../../src/RepoScanner.Scanner/RepositoryScanRunner.cs)), reading a drive's free space ([AzureDevOpsCloneSource.cs:133-135](../../src/RepoScanner.Scanner/Sources/AzureDevOpsCloneSource.cs)), and stopping git once it has already stopped or cannot be stopped ([GitProcess.cs:119-126 and 137-140](../../src/RepoScanner.Scanner/Git/GitProcess.cs)).
