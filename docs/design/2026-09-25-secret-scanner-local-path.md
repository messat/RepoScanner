# Issue 3: Secret scanner against a local path (design)

- **Status:** Draft, for review
- **Date:** 2026-09-25
- **Work item:** Issue 3, #14 (tasks #15 to #21 and #71)
- **Branch:** `feature/14-secret-scanner-against-a-local-path`
- **Amended:** 2026-09-25, with six fixes from the whole-branch review: files that cannot be read and folders that cannot be listed, placeholder references in code, SEC007 setting names, a failed save, and SEC001 in JSON

## Purpose

Issue 3 is the first vertical slice: the scanner finds hard-coded secrets in a folder on the developer's machine and stores them as findings in SQL, with no queue and no clone. It proves the path from scanner to findings to SQL end to end, and it can be demonstrated in the first week.

Task #16 refers to section 3.7 of *Code Repo Analysis Dashboard, Spike Findings & ADRs v2* for the skip rules, and task #17 names rules SEC001 to SEC007 without defining them. Neither is available, so, as the Issue 2 design did for section 3.5, this document is the source for both. They are drawn from the ADRs and the rest of the board, and each choice is recorded below.

## Success criteria

From Issue 3 (#14):

1. Running against the fixture writes the expected findings to SQL, with masked previews.
2. No raw secret value appears in a finding, a log line or test output.
3. Line coverage is at least 80% on `SecretScanner` and the rules.

From task #71:

4. A missing `Scanner:Database:ConnectionString` stops the scanner with exit code 2 and one log line naming the setting.
5. A test proves the `DbContext` and both repositories resolve from the host's services.
6. No connection string appears in any appsettings file, and the README configuration table lists the setting.

## Decisions

| # | Question | Decision | Reason |
|---|---|---|---|
| 1 | Where the rules and skip rules come from | This document, drawn from the ADRs and the board | Section 3.7 and the SEC001 to SEC007 definitions are not available |
| 2 | What a second `--path` run on the same folder does | An interim fingerprint, and findings already stored are skipped | The board is unchanged: the real fingerprint and reconciler stay in Issue 4 (#23, #24, #26), the demo can be run as often as needed, and about ten lines are replaced later |
| 3 | How a rule looks at a file | Each rule is a class with one regular expression, run on the whole text of the file | SEC001 spans several lines; each file is read once; #17 asks for separate classes |
| 4 | Which secrets are detected | SEC001 to SEC007, under Secret rules below | Chosen for the .NET and Azure repositories Blacklight scans |
| 5 | What is skipped | The list under Skip rules below | #49 names skip folders; #68 names files over 1 MB and binary files |
| 6 | The scan-exclusion marker (#21) | A folder holding `.reposcanner-exclude` is skipped with everything below it, except at the scan root | RepoScanner is one of the repositories scanned nightly, so its fake secrets must not reach the dashboard, while `--path` on the fixture still scans it |
| 7 | How much of a secret the preview shows | At most 4 characters and at most a quarter of its length, then always `****` | Never more than a quarter of the secret, and the length is not revealed |
| 8 | How the secret is hashed | Unsalted SHA-256, recorded in ADR-023 | The same secret must always give the same hash; see Masking and hashing |
| 9 | How `--path` is read | From the command-line arguments, not from `IConfiguration` | The host adds every environment variable to configuration, so Windows' `PATH` would appear as the setting `path` on every run |
| 10 | Queue settings in local mode | Not registered, so not required | Local mode reads no queue |
| 11 | The `Repositories` row for a local folder | A GUID made from the folder's full path, project name `Local` | The same folder always maps to the same row, and local rows are easy to tell apart from Azure DevOps ones |
| 12 | Where the file walker lives | `Scanner/Files/`, not `Scanner/Secrets/` | The package scanner (#43) and the code-pattern scanner (#49) use the same skip rules |
| 13 | How the regular expressions are built | `new Regex(pattern, options, TimeSpan.FromSeconds(1))`, not `[GeneratedRegex]` | The timeout stops a hostile file from hanging the scan; ADR-021 prefers code that is visible in the source over code a compiler feature adds |
| 14 | Where this document lives | `docs/design/`, next to the Issue 2 design | Same as Issue 2 |

## Project layout

```
src/RepoScanner.Core/                  still no packages
  Scanning/        IScanner, RawFinding                                  #15

src/RepoScanner.Scanner/
  Files/           RepositoryFileWalker, RepositoryFile                  #16
  Secrets/         SecretScanner                                         #16
                   ISecretRule                                           #17
                   Rules/  Sec001PrivateKeyRule, Sec002AzureSharedKeyRule,
                           Sec003ConnectionStringPasswordRule,
                           Sec004UrlCredentialsRule, Sec005EntraClientSecretRule,
                           Sec006NuGetApiKeyRule, Sec007NamedSettingRule #17
                   PlaceholderAllowList                                  #19
                   SecretMasker, SecretHasher                            #18
  Local/           LocalScanRunner, LocalPathArgument                    #20
  Configuration/   DatabaseOptions                                       #71

tests/fixtures/secrets-repo/           the fixture                       #21
tests/RepoScanner.Scanner.Tests/       + Microsoft.EntityFrameworkCore.Sqlite
  Files/           walker tests
  Secrets/         rule, allow-list, masking, scanner and fixture tests,
                   FixtureSecrets, .reposcanner-exclude
  Local/           end-to-end --path tests

docs/adr/ADR-023-secret-hash-is-an-unsalted-sha-256.md                   #18
```

- Classes are `sealed` and follow ADR-021, as in Issue 2: explicit `using` directives, block-scoped namespaces, explicit types, and a comment explaining the reason for anything that is not obvious.
- The rules, the walker and `SecretScanner` are singletons. `LocalScanRunner` is scoped, because the repositories it uses are scoped.

## Scanner contract (#15)

```csharp
namespace RepoScanner.Core.Scanning
{
    // One of the three scanners in the one scanner image (ADR-009)
    public interface IScanner
    {
        ScannerType ScannerType { get; }

        // Scans the files under rootPath and returns what it found. Never touches the database.
        Task<IReadOnlyList<RawFinding>> ScanAsync(string rootPath, CancellationToken cancellationToken);
    }
}
```

`RawFinding` is a `sealed record` with init-only properties, holding exactly the columns of `Finding` that a scanner can know:

| Property | Type | Notes |
|---|---|---|
| `ScannerType` | `ScannerType` | required |
| `RuleId` | `string` | required; `SEC001` and so on |
| `Severity` | `Severity` | required |
| `FilePath` | `string` | required; relative to the scanned folder, with `/` between folders |
| `LineNumber` | `int?` | counted from 1 |
| `Message` | `string` | required |
| `MaskedPreview` | `string?` | secret findings only |
| `SecretHash` | `string?` | secret findings only |
| `PackageName`, `PackageVersion`, `AdvisoryUrl` | `string?` | package findings only (Issue 7) |

Required properties use the `required` keyword, so a scanner cannot build a `RawFinding` without them. There is no property for the raw secret, so a raw value cannot be passed on or saved by mistake. The fingerprint is not on `RawFinding`: it is worked out from it, by interim code in Issue 3 and by `FingerprintCalculator` (#23) in Issue 4.

## Secret rules (#17)

Each rule is a sealed class implementing:

```csharp
public interface ISecretRule
{
    // SEC001 and so on
    string RuleId { get; }

    Severity Severity { get; }

    // Shown on the dashboard, for example "Private key"
    string Message { get; }

    // Must contain a group named "secret": the part that is masked and hashed
    Regex Pattern { get; }
}
```

| Rule | Message | Matches | The `secret` group | Severity |
|---|---|---|---|---|
| SEC001 | Private key | A block from `-----BEGIN <type>PRIVATE KEY-----` to the matching `-----END <type>PRIVATE KEY-----`, where `<type>` is empty, `RSA `, `EC `, `DSA `, `OPENSSH ` or `ENCRYPTED `, including a block written on one line with `\n` escapes, as in a JSON string | The text between the two lines, with whitespace and the escapes `\n` and `\r` removed, so that line endings and JSON escaping do not change the hash | Critical |
| SEC002 | Azure Storage or Service Bus key | `AccountKey=` or `SharedAccessKey=`, in any case, followed by at least 40 base64 characters | The base64 value | Critical |
| SEC003 | Password in a connection string | `Password=` or `Pwd=`, in any case and with spaces allowed around `=`, in a `key=value;` list on one line that also has a `Server`, `Data Source`, `Host` or `Address` key | The value, up to `;`, a quote or the end of the line | High |
| SEC004 | Credentials in a URL | `scheme://user:password@host`, where the user and password are not empty and contain no `/`, `@` or whitespace. A URL with a user name only, such as the clone URLs Azure DevOps offers (ADR-016), is not matched | The password | High |
| SEC005 | Entra ID client secret | 3 characters from `A-Z a-z 0-9 _ ~ .`, a digit, `Q~`, then 31 to 34 characters from the same set plus `-`, as a whole token | The whole token | High |
| SEC006 | NuGet API key | `oy2` followed by exactly 43 lower-case letters and digits, as a whole token | The whole token | High |
| SEC007 | Password, secret or key in a named setting | A setting whose name suggests a secret (see below), in one of three forms: a quoted value after `:` or `=`, as in JSON, YAML, C# and PowerShell; an XML pair, as in `<add key="ClientSecret" value="..." />`; or, at the start of a line, an unquoted `NAME=value` with no spaces around `=`, or `name: value`, as in `.env`, shell and YAML files | The value, without its quotes | Medium |

**Names SEC007 treats as secret.** The name, in any case, contains `password`, `passwd`, `pwd`, `secret`, `token`, `apikey`, `api_key`, `api-key`, `accesskey` or `access_key`; or it is `pat`, ends in `Pat` with a capital P, or ends in `_pat`, `-pat` or `.pat` in any case. So `AzureDevOpsPat` and `DEVOPS_PAT` count, while `Path` and `compat` do not. Names ending in `Url`, `Uri`, `Endpoint`, `Name`, `Path`, `Type`, `Length`, `Lifetime`, `Timeout`, `Hash`, `Pattern`, `Regex` or `Header` are left out, which removes common settings such as `TokenEndpoint`, `SecretName`, `PasswordHash`, `PasswordPattern` and `ApiKeyHeader`. The value must be at least 8 characters, contain no whitespace, and not be a URL (no `://`). An unquoted value also stops at `#`, `;`, `,`, `(` and `)`. Together with the "no spaces around `=`" rule, this means a line of C# such as `Password = options.Password;` is not read as a setting.

**Order and overlaps.** Rules run in `RuleId` order. When the `secret` groups of two matches in one file overlap, the match from the lower-numbered rule is kept, so a specific rule always wins over SEC007. For example, a storage connection string held in a setting called `StorageKey` is reported once, as SEC002.

**Options.** Every pattern uses `RegexOptions.CultureInvariant`, `RegexOptions.IgnoreCase` where the table says "in any case", and a match timeout of one second.

Each rule has at least one test it must catch and one look-alike it must ignore, including the look-alikes named above: a URL with a user name only, `Path`, `TokenEndpoint` and a value shorter than 8 characters.

## Skip rules (#16)

`RepositoryFileWalker` returns a `RepositoryFile(string FullPath, string RelativePath)` for every file under the scanned folder, in ordinal order of `RelativePath`, except:

| Skipped | Rule | Why |
|---|---|---|
| Folders by name | `.git`, `bin`, `obj`, `node_modules`, `packages`, `.vs` and `TestResults`, at any depth, with names compared ignoring case | Git internals, build output, downloaded packages, and IDE and test state. A clone rarely has them; a local folder usually does |
| Large files | Larger than 1 MB (1,048,576 bytes). A file of exactly 1 MB is scanned | #68. Real configuration and code files are far smaller |
| Binary files | A zero byte in the first 8,000 bytes | #68. This is the test git uses, so no list of extensions is needed |
| Symbolic links | Links to files or folders are not followed | A cloned repository could link to a file outside itself |
| Excluded folders | A folder containing `.reposcanner-exclude`, and everything below it, when that folder is below the scan root. A marker in the scan root itself is ignored | Decision 6 |
| Unreadable files and folders | A file that cannot be opened or read, for example because another program has locked it or it was deleted during the scan, and a folder that cannot be listed, are skipped with a warning naming them. Files are opened allowing other programs to go on writing to them, so a log file an application has open is still scanned | One file or folder should not stop the whole scan |

Everything else is scanned, whatever its extension. Files are read as UTF-8, honouring a byte order mark; bytes that are not valid UTF-8 are replaced rather than failing, so the rest of the file is still scanned. The walker reads the files too (`TryReadTextAsync`), so every scanner gets the same handling of a file that cannot be read. It logs one line with the number of files and folders skipped for each reason.

## Secret scanner (#16)

`SecretScanner` implements `IScanner` with `ScannerType.Secret`. It takes the walker, the rules (`IEnumerable<ISecretRule>`, sorted by `RuleId` so the order never depends on registration) and a logger. For each file:

1. Read the text once, through the walker, which skips the file with a warning if it cannot be read.
2. Run each rule's `Pattern` on the whole text, in rule order.
3. For each match, take the value of its `secret` group with all whitespace and the escapes `\n` and `\r` removed (only SEC001 and SEC003 values can contain any), and drop the match if the value is a placeholder (#19), including a value that is just the name of the setting it is assigned to (the rule's `name` group, which SEC003 and SEC007 have), or if it overlaps the secret of a match already kept in this file.
4. Work out the line number: 1 plus the number of line feeds before the match starts.
5. Mask and hash the value (#18) and build the `RawFinding`. The raw value goes no further.

Findings are returned sorted by file path, then line number, then rule ID. Cancellation is checked between files.

A `RegexMatchTimeoutException` affects only that rule on that file. The scanner logs a warning naming the file and the rule, without the exception object, because its `Input` property holds the file's text. It then carries on with the next rule.

When it finishes, the scanner logs one line with the number of files scanned, placeholders ignored and findings.

## Placeholder allow-list (#19)

`PlaceholderAllowList.IsPlaceholder(string value)` returns true when the whole value, ignoring case and surrounding whitespace, is one of these:

| Kind | Values |
|---|---|
| A placeholder word | `changeme`, `change-me`, `password`, `secret`, `placeholder`, `redacted`, `todo`, `none`, `null`, `example`, `dummy` |
| A reference to a value kept elsewhere | `${NAME}`, `$(NAME)`, `#{NAME}#` (Azure Pipelines token replacement), `%NAME%`, `{{NAME}}`, `{NAME}` (C# interpolation and `string.Format`), `__NAME__` and `<NAME>`, where `NAME` is anything that is not empty; `$env:NAME` (PowerShell); and `$NAME` in capital letters, digits and underscores (shell and pipeline variables) |
| The setting's own name | A value equal to the name of the setting it is assigned to, ignoring case and anything that is not a letter or digit: `Password=$password`, `AcquireToken: "acquireToken"` |
| One character repeated | `xxxxxxxx`, `********`, `00000000` |
| A value starting `your-` or `your_` | `your-api-key-here` |

Only whole values count: `changeme123` is reported, and so is a key that contains the word `EXAMPLE`. A `$name` in lower or mixed case is reported unless it is the setting's own name, because `$uperSecret1` could be a real password. The allow-list applies to every rule, although in practice only SEC003, SEC004 and SEC007 can match a placeholder, because the other rules need fixed formats.

## Masking and hashing (#18)

`SecretMasker.Mask(string secret)` returns the first `min(4, length / 4)` characters (integer division), followed by `****`:

| Secret | Characters shown | Preview |
|---|---|---|
| 88-character storage key | 4 | `abcd****` |
| 16 characters | 4 | `abcd****` |
| 8-character password | 2 | `Pa****` |
| 4 characters | 1 | `a****` |
| 3 characters or fewer | 0 | `****` |
| SEC001 key contents | 4 | `MIIE****`, the standard opening of almost every RSA key, so it gives nothing away |

`SecretHasher.Hash(string secret)` returns the SHA-256 of the secret's UTF-8 bytes as 64 lower-case hex characters (`Convert.ToHexStringLower`), which fits the `SecretHash` column (`varchar(64)`).

**ADR-023, added in #18: the secret hash is an unsalted SHA-256.**

- **Context:** a finding must recognise the same secret across scans and files without storing it. The Issue 2 design listed the limitation and gave the ADR to #18.
- **Decision:** unsalted SHA-256 of the secret's UTF-8 bytes.
- **Consequences:** anyone who can read the `Findings` table could recover a short or common secret by hashing guesses. Long random values, such as storage keys, private keys, NuGet keys and Entra client secrets, cannot be recovered that way. Only the scanner, dashboard and pipeline identities can read the table (ADR-008, ADR-014), and the data stays in UK West (ADR-015).
- **Rejected:** a random salt per finding, because the same secret would no longer give the same hash. A keyed hash (HMAC) with its key in Key Vault, because it adds a second Key Vault secret and ADR-007 allows exactly one. The keyed hash is recorded as the upgrade if short passwords turn out to be common among the findings.

## Keeping raw secrets out

Success criterion 2 names three places.

- **Findings.** Neither `RawFinding` nor `Finding` has a property for the raw value. It exists only while `SecretScanner` handles one match, which passes it to `PlaceholderAllowList`, `SecretMasker` and `SecretHasher` and nowhere else.
- **Logs.** Log lines name the rule, the file and the line, never the matched text or its preview. A timeout is logged without the exception object. No exception message the scanner creates contains file text, so `ScanRun.ErrorMessage` cannot contain any either.
- **Test output.** Leak checks use `Assert.That(leaked, Is.False, "<rule> value leaked into the log")`, because a failing `Does.Not.Contain(value)` would print the value in NUnit's failure message.

## Local mode: `--path` (#20)

```bash
dotnet run --project src/RepoScanner.Scanner -- --path tests/fixtures/secrets-repo
```

- `LocalPathArgument` reads `--path <folder>` or `--path=<folder>` from the arguments (decision 9). A relative folder is resolved against the current directory.
- `--path` without a folder, or with a folder that does not exist, is logged as one line and exits with code 2, before the host is built.
- With `--path`, `Program.CreateBuilder` registers `LocalScanRunner`, and does not register `QueueOptions` or `ScanOrchestrator` (decision 10). Without `--path`, the only change is that `DatabaseOptions` is now required.
- `Program.RunAsync` creates a service scope, runs `LocalScanRunner` in it with the `ApplicationStopping` token, and exits with 0 when the scan works, whatever it found.

`LocalScanRunner` depends on `IScanRunRepository`, `IFindingRepository`, `IEnumerable<IScanner>` and a logger. It runs every registered scanner, which in Issue 3 is only `SecretScanner`:

1. **Repository row.** The folder's `RepositoryDetails`:
   - `AzureDevOpsId`: the first 16 bytes of the SHA-256 of the folder's full path, lower-cased and without a trailing separator, as a `Guid`.
   - `ProjectName` is `Local`, `Name` is the folder's name, and `DefaultBranch` is `local`.
2. **Start.** `StartRunAsync` with `ScanTrigger.OnDemand` and a new correlation ID creates a `Running` scan run.
3. **Scan.** Each scanner runs on the folder, timed with a `Stopwatch`.
4. **Save the new findings (interim, decision 2).**
   - The fingerprint is the SHA-256, in lower-case hex, of `RuleId|FilePath|SecretHash`. It has no line number, as #27 will require.
   - Findings whose fingerprint is already stored for the repository (`GetByRepositoryAsync`) are skipped, and so is a repeat within the run: the same secret twice in one file is one finding.
   - Each new `Finding` is `Open`, with `FirstSeenUtc` and `LastSeenUtc` set to now, and a `Detected` event in its `Events` with `Actor` `scanner` and the run's `ScanRunId`.
   - Everything is saved in one `SaveChangesAsync`, with new findings only.
   - A comment marks this code as replaced by `FingerprintCalculator` (#23) and `FindingReconciler` (#24) when #26 wires them into local mode.
5. **Complete.** `CompleteRunAsync` with `Completed`, no commit SHA, `NewCount` as saved, `ReopenedCount` and `ResolvedCount` of 0, and one `ScannerResult` per scanner, with `Succeeded`, its finding count and its duration.
6. **Failure.** If a scan or the save throws, the runner calls `CompleteRunAsync` with `Failed` and the exception message, then rethrows, so `Program` logs the exception and exits with code 1. `FindingRepository.SaveChangesAsync` discards its pending changes when a save fails, so this second call does not try the failed findings again and can still record `Failed`. If that call fails too, the original exception is the one that continues.

It logs a start line with the folder and scan run ID, one line per new finding (rule, file and line), and a summary line with the number of findings found, new and already stored, and the duration.

## Database wiring (#71)

```csharp
public sealed record DatabaseOptions
{
    public const string SectionName = "Scanner:Database";

    [Required]
    public string ConnectionString { get; init; } = string.Empty;
}
```

- `DatabaseOptions` is registered in both modes with `Bind`, `ValidateDataAnnotations` and `ValidateOnStart` (ADR-022).
- `AddDbContext<ScannerDbContext>` uses the overload that receives the `IServiceProvider`, so it reads the validated options, and calls `UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure())`. This is the retry policy ADR-015 recommends, with EF Core's defaults: up to six retries, at most 30 seconds apart. The repositories use one `SaveChanges` per method and no explicit transactions, which the retrying strategy supports.
- `IScanRunRepository` maps to `ScanRunRepository` and `IFindingRepository` to `FindingRepository`, both scoped.
- Locally the value comes from the user environment variable `Scanner__Database__ConnectionString`, the same one `dotnet ef` reads. No appsettings file holds a connection string (ADR-007).
- Queue mode does not use the database yet, so the existing `ProgramTests` pass a dummy connection string. Nothing connects at start-up.

## README

- The configuration table gains `Scanner:Database:ConnectionString`: required, and set locally by the user environment variable.
- A new section, "Scan a local folder", gives the command, what is saved, what the marker file does, and that a second run adds nothing until Issue 4.
- The exit codes table notes that a missing or wrong `--path` folder exits with 2.

## Fixture (#21)

```
tests/fixtures/secrets-repo/
  .reposcanner-exclude             the marker, ignored here because this is the scan root
  README.md                        says every value is fake, and contains none
  src/Api/appsettings.json         SEC003, SEC007, and placeholders (changeme, #{DbPassword}#)
  src/Api/Web.config               SEC007 in an <add key="..." value="..." /> pair
  src/Api/StorageSettings.cs       SEC002, SEC005
  deploy/signing-key.pem           SEC001
  deploy/publish.ps1               SEC004, SEC006
  deploy/app.env                   SEC007 in the NAME=value form
  node_modules/tool/config.json    a secret in a skipped folder
  vendor/.reposcanner-exclude      a marker below the root,
  vendor/settings.json             so this secret is not reported
```

- Fake values contain `FAKE`, or `fake` where the format is lower-case, wherever the format allows, so a reader can tell they are fake. The allow-list only drops whole values, so they are still reported.
- The skipped folder is `node_modules/` rather than `bin/`, which `.gitignore` would leave out of the commit; for the same reason the environment file is `app.env`, not `.env`. The walker tests cover every skipped folder name.
- The expected findings (rule, file, line and fake value) are listed once, in `tests/RepoScanner.Scanner.Tests/Secrets/FixtureSecrets.cs`. Every test file that contains a fake secret lives under `tests/RepoScanner.Scanner.Tests/Secrets/`, which has its own `.reposcanner-exclude`, so the nightly scan of this repository reports none of them.
- GitHub Advanced Security is off for this repository (checked on 2026-09-25), so push protection does not block the fixture. If it is turned on, the fixture's values will need allowing there.

## Testing

Each rule, skip rule and method is written test first: a failing test, then the code that makes it pass.

| Task | Tests |
|---|---|
| #17 | Each rule catches at least one example and ignores at least one look-alike; SEC001 on one line with JSON escapes; SEC007 names ending in `Hash`, `Pattern`, `Regex` and `Header`; this repository's own `src`, `tests` and `docs` folders give no findings |
| #19 | Each kind of placeholder is recognised, including C#, PowerShell and shell variable references and a value that is the setting's own name; `changeme123` and `$uperSecret1` are not |
| #18 | The preview for secrets of 3, 4, 8, 16 and 88 characters; the SHA-256 of `abc` matches the published test value |
| #16 | Walker, in temporary folders: each skipped folder name; 1,048,576 bytes scanned and 1,048,577 skipped; a zero byte in the first 8,000 bytes skipped and one after them scanned; the marker ignored at the root and honoured below it; forward slashes and a fixed order; a symbolic link not followed, marked inconclusive if Windows does not let the test create one; a locked file skipped with a warning; a file another program has open for writing still listed; a file deleted or locked partway after the walk, and a folder that cannot be listed, skipped with a warning. Scanner: line numbers, the same with Windows line endings; a file that is not valid UTF-8 still scanned; the lower-numbered rule winning an overlap; placeholders dropped; a rule that times out logs a warning while the other rules still report |
| #21, #18 | Scanning the fixture gives exactly the findings in `FixtureSecrets`; no fake value appears in any `RawFinding` (checked through its `ToString()`) or in the captured log output |
| #71 | A missing connection string exits with 2 and one line naming it; `ScannerDbContext` and both repositories resolve from the host's services; no appsettings file has a `Scanner:Database` section |
| #20 | `Program.RunAsync` with `--path` on the fixture and in-memory SQLite in place of SQL Server: exit code 0; the expected findings saved with previews, hashes and `Detected` events; a `Completed` run with one `Succeeded` scanner result; a second run adds nothing and exits with 0; a missing folder, and `--path` with no folder, exit with 2; no fake value in the saved rows or the logs; a save that fails still marks the run `Failed` |

- Scanner.Tests gains `Microsoft.EntityFrameworkCore.Sqlite`. The end-to-end test replaces the SQL Server registration with SQLite through `RunAsync`'s `configureServices`, and creates the schema with `EnsureCreated()`, which test projects may call (Issue 2, task #13).
- The 80% gate on the Scanner project covers `SecretScanner` and the rules, which is success criterion 3. Core gains no logic.
- Manual check: run the command against `.\SQLEXPRESS` twice, and check in SSMS that the second run added no findings.

## Order of work

One task at a time, stopping after each for review, then a commit whose message starts "This commit" and ends in its task number (ADR-020):

1. **#14** This design document, and "SQL Server 2022" corrected to "SQL Server 2019" in the ADR-017 amendment and the Issue 2 design. The local instance is version 15.0, which is SQL Server 2019 Express; the decision is unchanged.
2. **#15** `IScanner` and `RawFinding`.
3. **#17** `ISecretRule` and the seven rules, each test first.
4. **#19** `PlaceholderAllowList`.
5. **#18** `SecretMasker`, `SecretHasher` and ADR-023.
6. **#16** `RepositoryFileWalker` and `SecretScanner`.
7. **#21 with #18** The fixture, both markers, `FixtureSecrets`, and the fixture and leak tests.
8. **#71** `DatabaseOptions` and the database registrations.
9. **#20** `LocalPathArgument`, `LocalScanRunner`, the end-to-end tests and the README.

## Not in Issue 3

| Item | Where it is on the board |
|---|---|
| The real fingerprint, and reconciliation (resolve, reopen, update `LastSeenUtc`) | Issue 4, tasks #23 to #27 |
| Queue mode, scan run lifecycle in the loop, the time budget | Issue 5, tasks #29 to #35 |
| Cloning | Issue 6, tasks #37 to #41 |
| The package and code-pattern scanners, which reuse `IScanner` and `RepositoryFileWalker` | Issues 7 and 8 |
| The container and its SQL login | Issue 9, tasks #57 to #60 and #70 |
| Correlation ID in the log context, JSON logs, the end-of-run summary | Issue 10, tasks #62 to #65 |
| An empty repository, a repository with no `.csproj`, a huge line; the mutation check on two rules | Issue 11, tasks #68 and #69 |
| A `Scanner:Rules` settings group | Not added: nothing reads it (ADR-022) |

## Known limitations

- Only the files as they are now are scanned, not git history (ADR-016).
- A secret split across strings or lines, or built at run time, is missed. SEC007 depends on the setting's name, so a secret in an oddly named setting is missed, and a harmless value in a secret-sounding one is reported; suppression handles those (ADR-019).
- UTF-16 files contain zero bytes, so they are skipped as binary, as git treats them.
- The hash is unsalted (ADR-023).
- The local repository row is found by the folder's path, so moving or renaming the folder gives it a new row.
- Findings saved in Issue 3 use the interim fingerprint. Once Issue 4's fingerprint is in place, local findings from Issue 3 may need clearing from the local database.
