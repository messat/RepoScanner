# Issue 11: Test hardening and coverage gate, implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** ADR-029 records that this release runs locally with no CI/CD pipelines; an example scan request in `docs/contracts` is read by a scanner test; odd repositories (an empty folder, one huge line, a locked project file) and two `SnippetHash` naming cases are tested; CP001 flags a bare `Format(...)` or `Concat(...)` after `using static System.String;`; breaking SEC003 and CP001 is shown to fail tests; and the coverage table is ready for the pull request.

**Architecture:** Mostly tests over code that already exists, plus one rule change. CP001's `IsStringFormatOrConcat` gains a bare-name branch guarded by a new `HasUsingStaticString`, which reads the `using static` directives of the file and of the namespaces holding the call. Everything else is documents, a JSON file, tests and board text.

**Tech Stack:** .NET 10, Roslyn (`Microsoft.CodeAnalysis.CSharp` 5.9.0), NUnit 4, `Microsoft.Extensions.Diagnostics.Testing` (`FakeLogger`), in-memory SQLite (`SqliteTestDatabase`), coverlet.msbuild, the `az` CLI for the board.

**Spec:** [docs/design/2026-10-04-test-hardening-and-coverage-gate.md](../../design/2026-10-04-test-hardening-and-coverage-gate.md). Read it before starting; this plan argues from it. The design was committed as `05869df` (#66).

## Global Constraints

- Branch: `feature/66-test-hardening-and-coverage-gate` (already created from `develop`).
- **Commits:** one per task, only after the user has reviewed that task's diff. The message starts `This commit` and ends with the task number, for example `#120`. It never mentions Claude, Opus or any AI tool: no `Co-Authored-By` line.
- **Board:** set the task to Doing when its plan task starts and to Done when its commit is made: `az boards work-item update --org https://dev.azure.com/Blacklight-Internal --id <id> --state <Doing|Done>`. Issue #66 stays Doing until its pull request is merged. Push and open a pull request only when the user asks.
- **Board text** is changed with `az rest --method patch --resource 499b84ac-1321-427f-aa17-267ca6975798 --url https://dev.azure.com/Blacklight-Internal/Lumina/_apis/wit/workitems/<id>?api-version=7.1 --headers Content-Type=application/json-patch+json --body @<file>`, with the json-patch body in a UTF-8 file without a BOM. Read the description first and change only the named text.
- **Order:** #120, #101, #68, #69, #67 (design decision 2). Before them, this plan and the design's switch to the Issue 7 example message's GUIDs are committed together under #66, once reviewed.
- **No pipeline:** nothing in this Issue creates a pipeline, a build validation policy or an Azure resource (ADR-029).
- **Exact values:** the largest scanned file is `1048576` bytes; the example message's `correlationId` is `5b1c6f3e-8a2d-4e7f-9c1a-2d3e4f5a6b7c` and `repositoryId` `0f8fad5b-d9cb-469f-a165-70867728950e`; the coverage gate stays `80` line on Core and Scanner.
- **Code conventions (ADR-021):** classes `sealed` or `static`; explicit types, no `var`; block bodies; `using (...) { }` blocks; a comment giving the reason for each non-obvious line. Tests: NUnit, `Method_Condition_Result` names, `Assert.EnterMultipleScope()` for several asserts.
- No em dash (U+2014) in any file. British English.
- `dotnet build RepoScanner.sln` has zero warnings and `dotnet test RepoScanner.sln` passes with the coverage gate at the end of every task. Filtered runs add `-p:CollectCoverage=false`.

## Review Focus

Inputs the spec does not name, most likely to bite first. All are CP001's, the one behaviour change; each has a test in Task 4.

1. `using static System.String;` after a file-scoped namespace (`namespace Shop;`): the directive belongs to that namespace, so a bare `Format(...)` below it is flagged.
2. `global using static System.String;` in the same file: flagged, since it is one of the file's own usings.
3. A bare `Format(...)` that builds a log message, not SQL, in a file with the `using`: not flagged, because only the three SQL positions are checked.
4. `using static` of another type, such as `using static Shop.SqlText;`, with a bare `Format(...)`: not flagged.
5. The `using` in a sibling namespace block, `namespace A { using static System.String; } namespace B { ... }`: not flagged in `B`.

---

### Task 1: ADR-029 and the amendments (#120)

**Files:**
- Create: `docs/adr/ADR-029-this-release-runs-locally-without-pipelines.md`
- Modify: `docs/adr/ADR-005-immutable-image-tags.md`, `docs/adr/ADR-006-migrations-applied-by-the-pipeline.md`, `docs/adr/ADR-007-managed-identity-throughout.md`, `docs/adr/ADR-008-two-privilege-levels.md`, `docs/adr/ADR-020-branching-strategy-and-branch-policies.md`
- Modify: `docs/adr/README.md:5,13-16,28,36`
- Modify: `README.md:183,438,519`

**Interfaces:**
- Produces: the name `ADR-029`, cited by Tasks 2 to 5 and by the board.

- [ ] **Step 1: Set #120 to Doing**

- [ ] **Step 2: Write ADR-029**

The layout of ADR-028: title `# ADR-029: This release runs locally, without CI/CD pipelines`, then `- **Status:** Accepted`, `- **Date:** 2026-10-04`, `- **Source:** Decided on 2026-10-04 while planning Issue 11 (#66), task #120`, then Context, Decision and Consequences with the content of the design's ADR-029 section: the context (ADR-017; enqueuer and dashboard not built), the decision (local only on Azurite, SQL Server Express and Docker Desktop; no pipeline, no Azure resources; #72, #84 and #99 kept out of scope; the enqueuer and the dashboard keep their own repositories, the message format being the one thing shared, through `docs/contracts/scan-request.example.json`), and the consequences (tests run by hand before each merge; the Azure ADRs unchanged; the five amendments; bringing the pipeline Issues back is removing their `out-of-scope` tag).

- [ ] **Step 3: Add a dated amendment to each of the five ADRs**

Each gets an `## Amendments` section after Consequences (ADR-020 keeps any it has), with one paragraph starting `**2026-10-04 (Issue 11, ADR-029):**` and ending `The decision is otherwise unchanged.` Content, from the design's amendment table:

| ADR | Amendment |
|---|---|
| ADR-005 | This release has no pipeline, so no build ID: the scanner image is tagged by hand, `reposcanner:v1` (Issue 9), and is never pushed to a registry |
| ADR-006 | This release has no pipeline: migrations are applied locally with `dotnet ef database update` (README, Database and migrations). Expand/contract still applies |
| ADR-007 | This release has no pipeline, so nothing signs in with Workload Identity Federation. Managed identities and Key Vault wait with the Azure deployment |
| ADR-008 | Locally, `dotnet ef` applies migrations with the developer's own Windows login, and the container's `repo_scanner_local` login holds `db_datareader` and `db_datawriter` only, so the two levels hold without a pipeline identity |
| ADR-020 | This release has no pipeline, so no build validation policy: `dotnet test RepoScanner.sln` is run before a pull request is completed. The reviewer, linked work item and resolved comments policies stand |

- [ ] **Step 4: Update the ADR README**

Line 5: `The next new ADR is **ADR-030**.` Rows ADR-005, ADR-006, ADR-007, ADR-008 and ADR-020: status `Accepted, amended 2026-10-04 (ADR-029)`. After ADR-028's row: `| [ADR-029](ADR-029-this-release-runs-locally-without-pipelines.md) | This release runs locally, without CI/CD pipelines | Accepted |`.

- [ ] **Step 5: Update the README's three pipeline mentions**

- Line 183, Who applies migrations: `- **Azure:** not in this release ([ADR-029](docs/adr/ADR-029-this-release-runs-locally-without-pipelines.md)). When it comes, only the pipeline, which ...` (the rest of the line unchanged).
- Line 438, Alerts: `The alert rules are not in this release ([ADR-029](docs/adr/ADR-029-this-release-runs-locally-without-pipelines.md)); when a pipeline exists it creates them (Issue 12). They use these two searches, and tests fail if either line they rely on changes.`
- Line 519, Logs known limitations: `- The searches are checked against Log Analytics only once the job runs in Azure, which is not in this release (ADR-029); until then, tests check the JSON lines they rely on.`

- [ ] **Step 6: Change the board wording on #66, #67 and #69**

| Item | Replace | With |
|---|---|---|
| #66 | `No pipeline exists until Issue 12, so this issue proves the coverage gate and the mutation check with dotnet test locally; running them in CI moved to Issue 12.` | `This release has no CI/CD pipeline (ADR-029), so this issue proves the coverage gate and the mutation check with dotnet test locally.` |
| #66 | ` (failing CI moved to Issue 12)` | ` (local only, ADR-029)` |
| #67 | `Running the gate in CI on every pull request moved to Issue 12.` | `This release has no CI (ADR-029), so the gate runs locally only.` |
| #69 | `Running it in CI is part of Issue 12.` | `This release has no CI (ADR-029).` |

Each replace must find its text exactly once; stop and report if not.

- [ ] **Step 7: Verify**

Run: `git grep -n "ADR-029" -- docs README.md` shows ADR-029 itself, the five amendments, the README table row and the three README lines. `git grep -n -P "\x{2014}" -- docs/adr README.md` (an em dash) shows nothing. `dotnet test RepoScanner.sln` passes.

- [ ] **Step 8: Review, commit, set #120 to Done**

```bash
git add docs/adr README.md
git commit -m "This commit adds ADR-029: this release runs locally with no CI/CD pipelines, the enqueuer and the dashboard keeping their own repositories; with dated amendments to ADR-005, ADR-006, ADR-007, ADR-008 and ADR-020, the ADR README, and the README's migrations, alerts and Log Analytics lines #120"
```

---

### Task 2: Example scan request (#101)

**Files:**
- Create: `docs/contracts/scan-request.example.json`
- Modify: `tests/RepoScanner.Scanner.Tests/Queue/ScanRequestReaderTests.cs`
- Modify: `docs/design/2026-09-29-queue-consumer-and-execution-loop.md:93-104` (Message format)

**Interfaces:**
- Consumes: `ScanRequestReader.TryRead(string body, out ScanRequest? request, out string? problem)`, `RepositoryRoot.FullPath`.
- Produces: the path `docs/contracts/scan-request.example.json`, which the enqueuer (#81) and the dashboard (#88) copy.

- [ ] **Step 1: Set #101 to Doing**

- [ ] **Step 2: Write the failing test**

The file needs `using System.IO;` (ImplicitUsings is off).

```csharp
// The file the enqueuer and the dashboard copy for their contract tests (#101). If it stops being accepted,
// they would be copying a message the scanner poisons.
[Test]
public void TryRead_TheExampleInDocs_IsAccepted()
{
    string body = File.ReadAllText(Path.Combine(RepositoryRoot.FullPath, "docs", "contracts", "scan-request.example.json"));

    bool read = ScanRequestReader.TryRead(body, out ScanRequest? request, out string? problem);

    using (Assert.EnterMultipleScope())
    {
        Assert.That(read, Is.True);
        Assert.That(problem, Is.Null);
        Assert.That(request, Is.EqualTo(new ScanRequest(
            new Guid("5b1c6f3e-8a2d-4e7f-9c1a-2d3e4f5a6b7c"),
            new Guid("0f8fad5b-d9cb-469f-a165-70867728950e"),
            "Lumina",
            "RepoScanner",
            "develop")));
    }
}
```

- [ ] **Step 3: Run it to see it fail**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~ScanRequestReaderTests" -p:CollectCoverage=false`
Expected: FAIL with `DirectoryNotFoundException` or `FileNotFoundException` for `scan-request.example.json`.

- [ ] **Step 4: Create the file**

The JSON block of the Issue 7 design's Message format section, byte for byte: six fields, two-space indent, UTF-8 without a BOM, ending in a newline.

- [ ] **Step 5: Run it to see it pass**

Same command. Expected: PASS.

- [ ] **Step 6: Point the Issue 7 design at the file**

Directly under `## Message format (#29)`: `*2026-10-04 (Issue 11, #101): this example is also [docs/contracts/scan-request.example.json](../contracts/scan-request.example.json), which a scanner test reads. The enqueuer and the dashboard copy that file for their contract tests.*`

- [ ] **Step 7: Full run, review, commit, set #101 to Done**

```bash
git add docs/contracts/scan-request.example.json tests/RepoScanner.Scanner.Tests/Queue/ScanRequestReaderTests.cs docs/design/2026-09-29-queue-consumer-and-execution-loop.md
git commit -m "This commit adds the example scan request as docs/contracts/scan-request.example.json, the file the enqueuer and the dashboard copy for their contract tests, with a scanner test that reads it and accepts every field, and a pointer to it from the Issue 7 design's message format #101"
```

---

### Task 3: Edge cases (#68)

**Files:**
- Modify: `tests/RepoScanner.Scanner.Tests/Local/LocalModeTests.cs`
- Modify: `tests/RepoScanner.Scanner.Tests/Packages/ProjectFileReaderTests.cs`
- Modify: `tests/RepoScanner.Scanner.Tests/CodePatterns/SnippetHashTests.cs`

**Interfaces:**
- Consumes: `LocalModeTests.RunAsync(params string[])`, `SavedFindings()`, `LastRun()`, `_database`, `_nuGet`; `TempFolder.WriteText(string, string)`; `ProjectFileReaderTests.ReadAsync()` and `WriteProject(string, params string[])`; `SnippetHashTests.FlagCall(string)` and `Sha256Hex(string)`; `SecretHasher.Hash(string)`.

These tests pin behaviour that already exists, so they pass on first run. **If the huge-line test fails because a rule timed out, stop and report to the user before changing any pattern** (design decision 5).

- [ ] **Step 1: Set #68 to Doing**

- [ ] **Step 2: Write the empty-folder test in `LocalModeTests`**

```csharp
// Issue 11 (#68): an empty repository through the whole program. The walker's own test covers an empty
// folder; this one checks every scanner and the saved run.
[Test]
public async Task Path_OnAnEmptyFolder_CompletesWithNoFindings()
{
    using (TempFolder folder = new TempFolder())
    {
        (int exitCode, _) = await RunAsync("--path", folder.FullPath);

        using (ScannerDbContext context = _database.CreateContext())
        {
            ScanRun run = context.ScanRuns.Include(saved => saved.ScannerResults).Single();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(exitCode, Is.EqualTo(0));
                Assert.That(run.Status, Is.EqualTo(ScanRunStatus.Completed));
                Assert.That(run.NewCount, Is.Zero);
                Assert.That(context.Findings, Is.Empty);
                Assert.That(run.ScannerResults, Has.Count.EqualTo(3));
                Assert.That(run.ScannerResults.Select(result => result.Status), Is.All.EqualTo(ScannerStatus.Succeeded));
                Assert.That(run.ScannerResults.Select(result => result.FindingCount), Is.All.EqualTo(0));
                Assert.That(_nuGet.Requests, Is.Empty);
            }
        }
    }
}
```

- [ ] **Step 3: Write the huge-line test in `LocalModeTests`**

The file is `wwwroot/app.min.js`: one line of exactly 1,048,576 ASCII bytes, made of `{"id":1,"name":"item"},` (23 characters) repeated and cut to length, then the connection string at the end. The comma before `Server` keeps a word boundary wherever the filler is cut. `SelfScanTests` scans `tests/`, and only `Secrets/` is excluded, so the connection string is joined from separate literals: a quote between them stops SEC003 matching this source file.

```csharp
// Issue 11 (#68): a minified file is one long line. Each secret pattern stops after 1 second and is then
// skipped for that file, so a slow pattern would miss a secret at the end without failing the scan.
[Test]
public async Task Path_OnAFileWithOneHugeLine_FindsTheSecretAtItsEnd()
{
    // The largest file the walker scans
    const int LargestScannedFile = 1048576;
    const string Password = "FAKE-Huge-Line-Pa55word";

    // Separate literals, so this repository's own scan does not report the connection string in this file
    string tail = ",Server=db01;Database=Orders;" + "Password=" + Password;
    string filler = string.Concat(Enumerable.Repeat("{\"id\":1,\"name\":\"item\"},", 50000));

    using (TempFolder folder = new TempFolder())
    {
        string path = folder.WriteText("wwwroot/app.min.js", filler.Substring(0, LargestScannedFile - tail.Length) + tail);

        (int exitCode, string output) = await RunAsync("--path", folder.FullPath);

        List<Finding> findings = SavedFindings();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(new FileInfo(path).Length, Is.EqualTo(LargestScannedFile));
            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(findings.Select(finding => (finding.RuleId, finding.FilePath, finding.LineNumber)),
                Is.EqualTo(new[] { ("SEC003", "wwwroot/app.min.js", (int?)1) }));
            Assert.That(findings.Single().SecretHash, Is.EqualTo(SecretHasher.Hash(Password)));
            Assert.That(LastRun().Status, Is.EqualTo(ScanRunStatus.Completed));
            Assert.That(output, Does.Not.Contain("timed out"));
        }
    }
}
```

- [ ] **Step 4: Write the locked project file test in `ProjectFileReaderTests`**

The file needs `using System;` and `using System.IO;` (ImplicitUsings is off).

```csharp
// Issue 11 (#68): another program locks part of a project file after the walk has read its start, so the
// read fails. Only Windows makes other readers respect a lock on part of a file.
[Test]
public async Task ReadAsync_AProjectFileThatCannotBeRead_IsSkippedAndTheOthersAreRead()
{
    if (OperatingSystem.IsWindows())
    {
        // Longer than the walker's 8,000-byte binary check, so the walk passes it and the read meets the lock
        string locked = _folder.WriteText("Locked/Locked.csproj", "<Project>\n<!-- " + new string('x', 20000) + " -->\n</Project>\n");
        WriteProject("Api/Api.csproj", "<PackageReference Include=\"System.Text.Json\" Version=\"8.0.3\" />");

        ProjectFileReadResult result;
        using (FileStream holder = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            holder.Lock(10000, 1000);
            result = await ReadAsync();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Packages.Select(package => package.PackageId), Is.EqualTo(new[] { "System.Text.Json" }));
            Assert.That(result.ProjectFileCount, Is.EqualTo(2));
            Assert.That(result.InvalidFileCount, Is.Zero);
        }
    }
    else
    {
        Assert.Ignore("Only Windows makes other readers respect a lock on part of a file");
    }
}
```

- [ ] **Step 5: Write the `SnippetHash` naming tests**

Two cases join `Compute_HashesTheTypeTheMemberAndTheTokens` (code on the type itself, design's `Compute_OnCodeOnTheTypeItself_UsesNoMemberName`):

```csharp
[TestCase("[Marker(Flag())] class Orders { }", "Orders||Flag ( )")]
[TestCase("class Money { public static Money operator +(Money a, Money b) { Flag(); return a; } }", "Money||Flag ( )")]
```

And one new test:

```csharp
// Code in the declaration but in no field's initialiser, such as an attribute on the field, takes the first
// field's name (#68)
[Test]
public void Compute_InAFieldDeclarationOutsideEveryInitialiser_UsesTheFirstFieldsName()
{
    string code = "class Orders { [Marker(Flag())] private readonly Action _first = null, _save = null; }";

    Assert.That(SnippetHash.Compute(FlagCall(code)), Is.EqualTo(Sha256Hex("Orders|_first|Flag ( )")));
}
```

- [ ] **Step 6: Run the new tests**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~LocalModeTests|FullyQualifiedName~ProjectFileReaderTests|FullyQualifiedName~SnippetHashTests|FullyQualifiedName~SelfScanTests" -p:CollectCoverage=false`
Expected: PASS, including `SelfScanTests` (nothing in the new test code reads as a secret).

- [ ] **Step 7: Check the lines are now covered**

Run `dotnet test RepoScanner.sln`, then read `tests/RepoScanner.Scanner.Tests/TestResults/coverage/coverage.cobertura.xml`: lines 60 to 62 of `Packages/ProjectFileReader.cs` and lines 56 and 68 of `CodePatterns/SnippetHash.cs` have `hits` above 0.

- [ ] **Step 8: Note the empty repository on enqueuer Issue 2 (#80)**

Append to #80's description: `<p><b>Agreed 2026-10-04 (scanner Issue 11):</b> leave out a repository with no default branch. Azure DevOps gives an empty repository none, and the scanner would fail its clone, try it again and poison the message every night. The scanner keeps a clean failed clone as a safety net. </p>`

- [ ] **Step 9: Review, commit, set #68 to Done**

```bash
git add tests/RepoScanner.Scanner.Tests/Local/LocalModeTests.cs tests/RepoScanner.Scanner.Tests/Packages/ProjectFileReaderTests.cs tests/RepoScanner.Scanner.Tests/CodePatterns/SnippetHashTests.cs
git commit -m "This commit adds Issue 11's edge-case tests: an empty folder through --path gives a Completed run with every scanner Succeeded and no findings; a SEC003 password at the end of a single 1 MB line is found with no rule timing out; a project file locked after the walk is skipped while the others are read; and SnippetHash names code on a type, in an operator and in a field's attribute #68"
```

---

### Task 4: CP001 and a bare `Format(...)`, then the mutation check (#69)

**Files:**
- Modify: `src/RepoScanner.Scanner/CodePatterns/Rules/Cp001SqlFromStringsRule.cs:63-75`
- Modify: `tests/RepoScanner.Scanner.Tests/CodePatterns/Rules/Cp001SqlFromStringsRuleTests.cs`
- Create: `tests/fixtures/patterns-repo/Data/StaticFormatQueries.cs`
- Modify: `tests/fixtures/patterns-repo/Data/UserQueries.cs` (append only, so existing line numbers stay)
- Modify: `tests/fixtures/patterns-repo/README.md:9,17`
- Modify: `tests/RepoScanner.Scanner.Tests/CodePatterns/FixturePatterns.cs`
- Modify: `docs/design/2026-09-28-code-pattern-scanner.md` (CP001 section)

**Interfaces:**
- Consumes: `PatternMatches.In(ICodePatternRule rule, string code)`, `SyntaxNames.LastIdentifier(SyntaxNode?)`.
- Produces: `private static bool HasUsingStaticString(SyntaxNode node)` in `Cp001SqlFromStringsRule`.

- [ ] **Step 1: Set #69 to Doing**

- [ ] **Step 2: Write the failing tests**

```csharp
// A bare Format or Concat is string's own after using static System.String, wherever that using sits (#69)
[TestCase("using static System.String; class R { void Run() { db.Database.ExecuteSqlRaw(Format(\"DELETE FROM Users WHERE Id = {0}\", id)); } }",
    "db.Database.ExecuteSqlRaw(Format(\"DELETE FROM Users WHERE Id = {0}\", id))")]
[TestCase("using static System.String; class R { void Run() { db.Users.FromSqlRaw(Concat(\"SELECT * FROM Users WHERE Name = '\", name, \"'\")); } }",
    "db.Users.FromSqlRaw(Concat(\"SELECT * FROM Users WHERE Name = '\", name, \"'\"))")]
[TestCase("using System; using static String; class R { void Run() { db.Database.ExecuteSqlRaw(Format(\"DELETE FROM Users WHERE Id = {0}\", id)); } }",
    "db.Database.ExecuteSqlRaw(Format(\"DELETE FROM Users WHERE Id = {0}\", id))")]
[TestCase("namespace Shop { using static System.String; class R { void Run() { db.Database.ExecuteSqlRaw(Format(\"DELETE FROM Users WHERE Id = {0}\", id)); } } }",
    "db.Database.ExecuteSqlRaw(Format(\"DELETE FROM Users WHERE Id = {0}\", id))")]
[TestCase("namespace Shop; using static System.String; class R { void Run() { db.Database.ExecuteSqlRaw(Format(\"DELETE FROM Users WHERE Id = {0}\", id)); } }",
    "db.Database.ExecuteSqlRaw(Format(\"DELETE FROM Users WHERE Id = {0}\", id))")]
[TestCase("global using static System.String; class R { void Run() { db.Database.ExecuteSqlRaw(Format(\"DELETE FROM Users WHERE Id = {0}\", id)); } }",
    "db.Database.ExecuteSqlRaw(Format(\"DELETE FROM Users WHERE Id = {0}\", id))")]
public void FindMatches_FlagsABareFormatOrConcatAfterUsingStaticString(string code, string expected)
{
    Assert.That(PatternMatches.In(_rule, code), Is.EqualTo(new string[] { expected }));
}

// Without that using, a bare Format is some other method; with it, only the three SQL positions count (#69)
[TestCase("class R { void Run() { db.Database.ExecuteSqlRaw(Format(\"DELETE FROM Users WHERE Id = {0}\", id)); } }")]
[TestCase("using static Shop.SqlText; class R { void Run() { db.Database.ExecuteSqlRaw(Format(\"DELETE FROM Users WHERE Id = {0}\", id)); } }")]
[TestCase("namespace A { using static System.String; } namespace B { class R { void Run() { db.Database.ExecuteSqlRaw(Format(\"DELETE FROM Users WHERE Id = {0}\", id)); } } }")]
[TestCase("using static System.String; class R { void Run() { string message = Format(\"Found {0} users\", count); } }")]
public void FindMatches_IgnoresABareFormatThatIsNotStrings(string code)
{
    Assert.That(PatternMatches.In(_rule, code), Is.Empty);
}
```

- [ ] **Step 3: Run them to see the first six fail**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~Cp001SqlFromStringsRuleTests" -p:CollectCoverage=false`
Expected: the six `FindMatches_FlagsABareFormatOrConcatAfterUsingStaticString` cases FAIL (no match); the four `Ignores` cases pass.

- [ ] **Step 4: Implement**

In `IsStringFormatOrConcat(InvocationExpressionSyntax call)`: the method name comes from a member access on `string`/`String` as today, or from a bare `IdentifierNameSyntax` when `HasUsingStaticString(call)`; the result is still `Format` or `Concat`. `HasUsingStaticString(SyntaxNode node)` is true when any `UsingDirectiveSyntax` in the `CompilationUnitSyntax` or in a `BaseNamespaceDeclarationSyntax` among `node.Ancestors()` has a `StaticKeyword` and `SyntaxNames.LastIdentifier(directive.NamespaceOrType) == "String"`. Update the class comment and the one above `IsStringFormatOrConcat` to name the bare form and its known limit: a `global using static` in another file, or an alias, is not seen.

- [ ] **Step 5: Run them to see them pass**

Same command. Expected: PASS, including every existing CP001 test.

- [ ] **Step 6: Plant the case in the patterns fixture**

`tests/fixtures/patterns-repo/Data/StaticFormatQueries.cs`, with the bare `Format(...)` on line 13 marked `// CP001`:

```csharp
using Microsoft.EntityFrameworkCore;
using static System.String;

namespace PatternsRepo.Data
{
    // CP001: SQL built with Format after using static System.String. Insecure on purpose: this file is never built.
    public class StaticFormatQueries
    {
        private readonly ShopContext _db;

        public int DeleteByEmail(string email)
        {
            return _db.Database.ExecuteSqlRaw(Format("DELETE FROM Users WHERE Email = '{0}'", email)); // CP001
        }
    }
}
```

Append to `UserQueries` (inside the class, after `RunSaved`) the matching not-a-finding: a comment `// Not a finding: this file has no using static System.String, so Format is this class's own method`, a method `FindByFormattedName(string name)` returning `_db.Users.FromSqlRaw(Format(name))`, and a `private static string Format(string name)` returning `"SELECT * FROM Users WHERE Name = @name"`.

`FixturePatterns.All`: insert `new FixturePattern("CP001", "Data/StaticFormatQueries.cs", 13),` after the Crypto entries and before the `Data/UserQueries.cs` ones (ordinal file order), with the comment `// A bare Format(...) after using static System.String; not this class's own Format in UserQueries`.

Fixture README: the `Data/` row's findings become `6: ..., a bare Format(...) after using static System.String` and its not-findings gain `a class's own Format method`; `That is 22 findings in all.`

- [ ] **Step 7: Note CP001's change in the Issue 6 design**

Under `### CP001: SQL built from strings (#50)`, after the Flags list: `*2026-10-04 (Issue 11, #69): a bare Format(...) or Concat(...) is flagged too when the file, or a namespace holding the call, has using static System.String. A global using static in another file, or an alias such as using S = System.String, is not seen, because the rule reads one file at a time.*`

- [ ] **Step 8: Full run**

Run: `dotnet build RepoScanner.sln` then `dotnet test RepoScanner.sln`. Expected: zero warnings; all pass, including `FixturePatternScanTests` and `Path_OnThePatternsFixture_SavesExactlyThePlantedPatternsOnce` with 22 patterns, and the self-scan tests.

- [ ] **Step 9: Review and commit**

```bash
git add src/RepoScanner.Scanner/CodePatterns/Rules/Cp001SqlFromStringsRule.cs tests/RepoScanner.Scanner.Tests/CodePatterns tests/fixtures/patterns-repo docs/design/2026-09-28-code-pattern-scanner.md
git commit -m "This commit has CP001 flag SQL built with a bare Format or Concat call when the file, or a namespace holding the call, has using static System.String, and not a bare Format without it or outside the SQL positions; plants one such call in the patterns fixture beside a class's own Format; and notes the change and its limit in the Issue 6 design #69"
```

- [ ] **Step 10: The mutation check, by hand**

With the working tree clean after the commit:

1. In `Sec003ConnectionStringPasswordRule.cs`, change `(?<name>Password|Pwd)` to `(?<name>Passwrd|Pwd)`. Run `dotnet test tests/RepoScanner.Scanner.Tests -p:CollectCoverage=false --tl:off` and keep the summary line and the names of the failing tests. Expected: several fail, at least `Pattern_FindsThePassword` and the secrets fixture tests.
2. `git checkout -- src/RepoScanner.Scanner/Secrets/Rules/Sec003ConnectionStringPasswordRule.cs`.
3. In `Cp001SqlFromStringsRule.cs`, change the `+` case in `IsBuiltFromStrings` from `=> !JoinsOnlyLiterals(join)` to `=> false`. Run the same command and keep the same output. Expected: several fail, at least `FindMatches_FlagsSqlBuiltFromStrings` and the patterns fixture tests.
4. `git checkout -- src/RepoScanner.Scanner/CodePatterns/Rules/Cp001SqlFromStringsRule.cs`, run the same command: all pass. `git status --short` shows nothing.

If either change makes no test fail, stop: that is a gap, and the missing test is added and committed under #69 before going on.

- [ ] **Step 11: Record the result on #69, set #69 to Done**

Append to #69's description a dated paragraph with, for each rule, the one-line change, the failing count and test names, and the green run after `git checkout`. The pull request's "How it was tested" copies it.

---

### Task 5: The coverage table (#67)

**Files:** none.

- [ ] **Step 1: Set #67 to Doing**

- [ ] **Step 2: Run every test with the coverage tables showing**

Run: `dotnet test RepoScanner.sln --tl:off`
Expected: all pass; one table each for `RepoScanner.Core`, `RepoScanner.Data` and `RepoScanner.Scanner`, with Core and Scanner at 80% line or more.

- [ ] **Step 3: Record the table on #67, set #67 to Done**

Append to #67's description a dated table of Line, Branch and Method for the three projects, with the test counts and the commit it was run on (`git rev-parse --short HEAD`). The pull request's "How it was tested" copies it. Nothing is committed for this task.

- [ ] **Step 4: Report**

Tell the user Issue 11's tasks are done, with the five commits, and that the branch is ready to push and open a pull request when they ask. #66 stays Doing until that pull request is merged.
