# Issue 6: Code pattern scanner (design)

- **Status:** Draft, for review
- **Date:** 2026-09-28
- **Work item:** Issue 6, #48 (tasks #49 to #55)
- **Branch:** `feature/48-code-pattern-scanner`
- **Amended:** 2026-09-29, with #49: decision 17, after a probe showed Roslyn's parser can overflow the stack on deeply nested code, and the rules no longer run in a set order

## Purpose

The scanner finds insecure patterns in the C# files of a folder and reports each as a finding, `CP001` to `CP006`, with a message that says what is wrong, what to do instead, and the Microsoft guidance behind the rule. It is the third and last scanner type (ADR-009), and the only one that reads code rather than text or project files.

"Insecure code patterns" is broad, and SonarQube already covers code quality at Blacklight. The rule set is therefore kept small and aimed at security: four patterns that can lead to a real attack, and one common habit that hides failures. Every rule is a lookup of names in the parsed code, with no tracing of values through it, so each can be built and reviewed on its own in the time left.

ADR-018 lists candidate rules and says the rule set must be confirmed with the technical manager. This document sets the final list, and ADR-018 is amended to match (#55). *Code Repo Analysis Dashboard, Spike Findings & ADRs v2* is not available, as noted in the Issue 3 design, so this document is the source for the rules. Each choice is recorded below.

## Success criteria

From Issue 6 (#48):

1. Every rule has at least one true positive and one true negative test.
2. A run over the fixture produces exactly the planted findings and no others.

## Decisions

| # | Question | Decision | Reason |
|---|---|---|---|
| 1 | Where the rule set comes from | This document; ADR-018 is amended with it (#55) | ADR-018 lists candidates only, and the spike document is not available |
| 2 | How many rules | Six, `CP001` to `CP006` | ADR-018 and the board already say six. Each is about a day's work with its tests, and Issues 7 and 8 still follow |
| 3 | Which rules | SQL built from strings, insecure deserialization, certificate validation switched off, weak hashing, `AllowAnyOrigin()`, and empty `catch` blocks (see Rules) | Security problems that are common in .NET and Dynamics 365 code, each found by a name lookup |
| 4 | What is left out | `new HttpClient()` per call; `Random` for security tokens; cross-site scripting; null references, dead code, infinite loops, resource leaks, duplicate code and long methods | `HttpClient` is reliability, not security. A syntax-only rule cannot tell a token from a dice roll, so it would flag every `Random`. Cross-site scripting lives in `.razor` and `.cshtml` files, which are not parsed. The rest need values traced through the code or a view of the whole project, are code quality rather than security, and SonarQube already reports them |
| 5 | What a rule cites | The Microsoft analyser ID it mirrors, with its Microsoft Learn page. `CP005` cites the *Enable CORS* page, since no analyser covers it | ADR-018. No ID is made up: an invented CA number could clash with a real one or lead nowhere |
| 6 | Where the reference is stored | At the end of the message: `(CA2100: https://learn.microsoft.com/...)` | No database or dashboard change. `AdvisoryUrl` stays a package column |
| 7 | Which files are read | Files whose name ends in `.cs`, ignoring case, listed by `RepositoryFileWalker` | The Issue 3 skip rules apply (`bin`, `obj`, `.reposcanner-exclude`, files over 1 MB) |
| 8 | Test code | Scanned like any other code | As for secrets and packages. A harmless finding in a test is suppressed with a reason (ADR-019) |
| 9 | How a file is parsed | Once, with `CSharpSyntaxTree.ParseText`, the latest language version and no preprocessor symbols; every rule then walks the same tree | ADR-018. Parsing is the slow part |
| 10 | How a rule is built | One class per rule with its own `CSharpSyntaxWalker`, behind `ICodePatternRule` | As `ISecretRule` in Issue 3: each rule is small, tested alone and reviewed alone |
| 11 | The severities | `CP001`, `CP002`, `CP003` High; `CP004`, `CP005` Medium; `CP006` Low. Never Critical | A leaked key is usable at once, so secrets reach Critical. A code pattern is a risk only when an attacker can reach it |
| 12 | How far `CP001` looks | Only at the SQL written in the call itself, not a variable built earlier | Tracing a variable back is flow analysis, which ADR-018 leaves out |
| 13 | What `CP002` covers | `BinaryFormatter`, `LosFormatter`, `NetDataContractSerializer`, `ObjectStateFormatter`, and `TypeNameHandling` other than `None`. `SoapFormatter` is left out | Newtonsoft's `TypeNameHandling` is the case most likely in current Dynamics code. `SoapFormatter` has no analyser ID (decision 5) |
| 14 | An empty `catch` holding a comment | Not a finding | The comment records a deliberate choice, such as `// the file may already be gone` |
| 15 | What identifies a finding | The hash of the containing type, the containing member and the matched code's tokens. ADR-024 is amended with the recipe (#49) | Spacing and comments do not change it, lines moving do not change it, and two empty `catch` blocks in different methods stay two findings |
| 16 | The same match twice in one member | One finding | As for a secret (ADR-024). Rare outside `CP006` |
| 17 | A file too deeply nested to scan | Not parsed when its parentheses and angle brackets nest more than 200 deep (`NestingDepth`); skipped when the parser stops with error CS8078 or a walker throws `InsufficientExecutionStackException`. Each is skipped with a warning; the other files are scanned | A probe on 2026-09-29 with Roslyn 5.9.0 found three behaviours on code nested a few thousand deep. A long `+` or `a.b.c` chain throws `InsufficientExecutionStackException`. Deep blocks, `!` or ternaries make the parser stop with CS8078 and an empty tree. Deep `(` or `<` overflow the stack in the parser, from about 1,500, which ends the whole process and cannot be caught. 200 is about seven times below that, and far deeper than code written by hand |
| 18 | The Roslyn package | `Microsoft.CodeAnalysis.CSharp` **5.9.0**, the latest stable version on 2026-09-28, in `RepoScanner.Scanner` only | The syntax tree and walker come from it. Core stays free of packages |
| 19 | A settings group for the scanner | None | ADR-022 adds a group only when something reads it |
| 20 | New ADRs | None. ADR-018 and ADR-024 are amended | The decisions stand; only the rule list and the hash recipe are made exact |
| 21 | Where this document lives | `docs/design/` | Same as Issues 2 to 5 |

## Project layout

```
src/RepoScanner.Scanner/                 + Microsoft.CodeAnalysis.CSharp
  CodePatterns/    ICodePatternRule, CodePatternMatch, CodePatternScanner,
                   SnippetHash, NestingDepth, SyntaxNames, RuleReference      #49
                   Rules/  Cp001SqlFromStringsRule                            #50
                           Cp002InsecureDeserializationRule                   #51
                           Cp003CertificateValidationDisabledRule             #52
                           Cp004WeakHashingRule, Cp005AnyOriginRule,
                           Cp006EmptyCatchRule                                #53
  Program.cs       registers the rules and the scanner                        #49 to #53

tests/fixtures/patterns-repo/            the fixture                          #54
tests/RepoScanner.Scanner.Tests/
  CodePatterns/    rule, hash, scanner and fixture tests, FixturePatterns

docs/adr/ADR-018-code-pattern-rules-from-a-roslyn-syntax-walker-justified-by-analyser-ids.md  amended  #55
docs/adr/ADR-024-fingerprint-per-scanner-without-line-numbers.md                             amended  #49
```

- Classes are `sealed` or `static` and follow ADR-021, as in Issues 2 to 5. The rules and `CodePatternScanner` are singletons, like the other scanners: a rule creates a new walker for each file, so it keeps nothing between scans.

## Scanner and rule contract (#49)

```csharp
namespace RepoScanner.Scanner.CodePatterns
{
    // One piece of code a rule flags, and the message for it, which ends with the rule's reference
    public sealed record CodePatternMatch(SyntaxNode Node, string Message);

    // One insecure pattern, CP001 to CP006 (Issue 6 design, Rules). CodePatternScanner parses each file once
    // and gives every rule the same tree.
    public interface ICodePatternRule
    {
        // CP001 and so on. Also the order the rules run in.
        string RuleId { get; }

        Severity Severity { get; }

        IReadOnlyList<CodePatternMatch> FindMatches(SyntaxNode root);
    }

    public sealed class CodePatternScanner : IScanner
    {
        public CodePatternScanner(RepositoryFileWalker walker, IEnumerable<ICodePatternRule> rules, ILogger<CodePatternScanner> logger);

        public ScannerType ScannerType => ScannerType.CodePattern;

        public Task<IReadOnlyList<RawFinding>> ScanAsync(string rootPath, CancellationToken cancellationToken);
    }
}
```

1. List the files with `RepositoryFileWalker` and keep those ending in `.cs` (decision 7).
2. Read each with `TryReadTextAsync`. A file that cannot be read is skipped and counted, as in the secret scanner.
3. Skip it, with a warning naming it, when `NestingDepth.Deepest` is over `MaxNestingDepth` (200). Otherwise parse it (decision 9), and skip it the same way when the parser stopped with CS8078 (decision 17). Broken C# still gives a tree, so the rules run on whatever parsed; there is no other special handling.
4. Run every rule. The order does not matter, because the findings are sorted. An `InsufficientExecutionStackException` skips the whole file with the same warning, including what an earlier rule found in it (decision 17). Any other exception is a bug in a rule: it fails the scanner, and the run carries on without it (Issue 5 design, Failed scanners).
5. Build one `RawFinding` per match:

| Property | Value |
|---|---|
| `ScannerType` | `CodePattern` |
| `RuleId`, `Severity` | from the rule |
| `FilePath` | the file's relative path, from the walker |
| `LineNumber` | the line where the matched code starts, counted from 1 |
| `Message` | from the match: fixed text for the rule, ending with its reference (see Rules). No code from the repository |
| `SnippetHash` | `SnippetHash.Compute(match.Node)` |

Findings are returned sorted by file path, line number and rule ID, and every one has the parts `FingerprintCalculator` requires for a code pattern. Cancellation is checked between files. The scanner logs one summary line: `.cs` files parsed, findings per rule, and files skipped as unreadable or too deep.

**Helpers.**

- `SyntaxNames.LastIdentifier(node)` gives the last name in a type or member access, so `System.Security.Cryptography.MD5` and `MD5` both give `MD5`.
- `SyntaxNames.AlwaysReturnsTrue(expression)` is true for a lambda or anonymous method whose body is the literal `true`, or a block holding only `return true;`. `CP003` and `CP005` share it.
- `RuleReference.ForAnalyser("CA2100")` gives `CA2100: https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca2100`. `RuleReference.EnableCors` gives `Enable CORS: https://learn.microsoft.com/aspnet/core/security/cors`.

**Wiring.** `Program.CreateBuilder` registers each rule as an `ICodePatternRule` and `CodePatternScanner` as a third `IScanner`, after the package scanner, so it runs last. `LocalScanRunner` already runs every registered scanner, so `--path` runs all three. The existing end-to-end tests that expect two `ScannerResult` rows are updated to expect three.

## Snippet hash (#49)

```csharp
namespace RepoScanner.Scanner.CodePatterns
{
    public static class SnippetHash
    {
        // SHA-256 in lower-case hex of "<type>|<member>|<tokens>", as UTF-8 (ADR-024 amendment)
        public static string Compute(SyntaxNode node);
    }
}
```

| Part | What it is | Example |
|---|---|---|
| Type | The names of the types the code sits in, outermost first, joined with `.`; empty in top-level statements | `UserRepository`, `Startup.CorsSetup` |
| Member | The name of the method, constructor, property, indexer (`this`), event or field the code sits in; empty when there is none. Code in an accessor, a lambda or a local function belongs to the member around it | `FindByName` |
| Tokens | The matched code's tokens (keywords, names, symbols, literals), joined by single spaces. Spacing, line breaks and comments are not tokens | `catch ( Exception ) { }` |

- Reformatting a file or moving code up or down it keeps every fingerprint, so suppressions follow the code.
- Renaming the type or member, or changing the matched code, such as the SQL text in a `CP001` call, makes the finding new, as renaming the file does today (ADR-024).
- Overloads share a member name, so identical matches in two overloads of one method are one finding (decision 16).

**ADR-024 amendment, added in #49.** *2026-09-28 (Issue 6): for a code-pattern finding, the hash of the matched code is the SHA-256 of the containing type, the containing member and the matched code's tokens joined by single spaces, joined with `|`. Spacing and comments do not change it; renaming the type or member does. The formula is unchanged.*

## Rules

All six rules match names in the parsed tree, so text in comments and string literals never matches. That includes the C# held in the test project's strings, so the nightly scan of RepoScanner does not report its own tests.

| Rule | Severity | Reference |
|---|---|---|
| `CP001` SQL built from strings | High | CA2100 |
| `CP002` Insecure deserialization | High | CA2300, CA2305, CA2310, CA2315, CA2326 |
| `CP003` Certificate validation switched off | High | CA5359 |
| `CP004` Weak hashing | Medium | CA5350, CA5351 |
| `CP005` Any origin allowed | Medium | Enable CORS |
| `CP006` Empty catch | Low | related to CA1031 |

A finding cites only the Microsoft reference (decision 5). In the messages below, `<link>` is the rule's Microsoft Learn page and `<reference>` is the ID with its link, both from `RuleReference`.

### CP001: SQL built from strings (#50)

**Where it looks.** The first argument of a call to `FromSqlRaw`, `ExecuteSqlRaw`, `ExecuteSqlRawAsync` or `SqlQueryRaw`; the first argument of `new SqlCommand(...)`; and the value assigned to `CommandText`, including inside an object initialiser.

**Flags** the SQL, after removing brackets around it, when it is:

- a `+` whose parts are not all string literals: `"SELECT ... '" + name + "'"`
- an interpolated string with at least one hole: `$"SELECT ... '{name}'"`
- a call to `string.Format` or `string.Concat`

**Does not flag:** `FromSql`, `FromSqlInterpolated`, `ExecuteSql` and `ExecuteSqlInterpolated`, which turn the holes into parameters; a plain literal; `"SELECT " + "1"`; SQL passed in a variable (decision 12).

**Message:** `SQL built by joining strings can allow SQL injection. Pass values as parameters, or use FromSql or ExecuteSql (CA2100: <link>)`

### CP002: Insecure deserialization (#51)

**Flags:**

| Code | Reference |
|---|---|
| `new BinaryFormatter(...)` | CA2300 |
| `new LosFormatter(...)` | CA2305 |
| `new NetDataContractSerializer(...)` | CA2310 |
| `new ObjectStateFormatter(...)` | CA2315 |
| `TypeNameHandling.Objects`, `.Arrays`, `.All` or `.Auto`, wherever it is written: in `JsonSerializerSettings` or in an attribute | CA2326 |

**Does not flag:** `TypeNameHandling.None`, `System.Text.Json`, `DataContractSerializer`, and `SoapFormatter` (decision 13).

**Messages:**

- `<Type> can run an attacker's code when it reads untrusted data. Use System.Text.Json instead (<reference>)`
- `TypeNameHandling other than None lets the JSON choose which type is created, which can run an attacker's code. Use TypeNameHandling.None (CA2326: <link>)`

### CP003: Certificate validation switched off (#52)

**Flags:**

- An assignment, `=` or `+=`, to `ServerCertificateCustomValidationCallback`, `ServerCertificateValidationCallback` or `RemoteCertificateValidationCallback` whose value always returns true (`SyntaxNames.AlwaysReturnsTrue`): `(message, certificate, chain, errors) => true`, `delegate { return true; }`
- Any use of `DangerousAcceptAnyServerCertificateValidator`

**Does not flag:** a callback that checks something, such as `errors == SslPolicyErrors.None`; a callback set to a named method, whose body the rule cannot see.

**Message:** `Certificate validation is switched off, so traffic can be read or changed in transit. Remove the callback, or check the certificate in it (CA5359: <link>)`

### CP004: Weak hashing (#53)

**Flags:**

| Code | Reference |
|---|---|
| `MD5.Create`, `MD5.HashData` or `MD5.HashDataAsync`; `DES.Create` or `RC2.Create` | CA5351 |
| `new MD5CryptoServiceProvider()`, `new DESCryptoServiceProvider()`, `new RC2CryptoServiceProvider()` | CA5351 |
| `SHA1.Create`, `SHA1.HashData` or `SHA1.HashDataAsync`; `TripleDES.Create` | CA5350 |
| `new SHA1CryptoServiceProvider()`, `new SHA1Managed()`, `new TripleDESCryptoServiceProvider()` | CA5350 |

**Does not flag:** `SHA256`, `SHA384`, `SHA512`, `Aes`, and the `HMAC` classes.

**Message:** `<Algorithm> is a weak algorithm. Use SHA-256 or stronger, or AES for encryption. If it is only a checksum, suppress this finding with that reason (<reference>)`

### CP005: Any origin allowed (#53)

**Flags:** a call to `AllowAnyOrigin()`, and a call to `SetIsOriginAllowed` whose argument always returns true, which allows every origin in the same way.

**Does not flag:** `WithOrigins("https://...")`, or `SetIsOriginAllowed` with a check.

**Message:** `AllowAnyOrigin lets any website call this API from a user's browser. List the allowed origins with WithOrigins (Enable CORS: <link>)`

### CP006: Empty catch (#53)

**Flags:** a `catch` whose block has no statements and no comment, with or without an exception type or a `when` filter.

**Does not flag:** a block holding a comment (decision 14); `catch { throw; }`; a block that logs or handles the exception.

**Message:** `This catch block is empty, so errors are hidden. Log or handle the exception, or add a comment saying why it is ignored (related to CA1031: <link>)`

CA1031 is about catching general exception types rather than empty blocks, so the reference says "related to", as Issue 6's notes already say.

## Fixture (#54)

```
tests/fixtures/patterns-repo/
  .reposcanner-exclude              ignored here, the scan root; honoured in the nightly scan of RepoScanner
  README.md                         says the insecure code is on purpose, and lists every planted finding
  Data/UserQueries.cs               CP001: each flagged form, and FromSql, a literal and a variable
  Serialization/LegacyCache.cs      CP002: each formatter, TypeNameHandling.Auto in settings and in an
                                    attribute, and TypeNameHandling.None
  Http/InsecureClient.cs            CP003: both lambda forms and DangerousAcceptAnyServerCertificateValidator,
                                    and a callback that checks the errors
  Crypto/Checksums.cs               CP004: MD5.Create, SHA1.HashData, new TripleDESCryptoServiceProvider,
                                    and SHA256.Create
  Web/CorsSetup.cs                  CP005: AllowAnyOrigin, SetIsOriginAllowed(_ => true), and WithOrigins
  Errors/Catches.cs                 CP006: two empty catches in different methods, one with a comment,
                                    catch { throw; }
  Quiet/OnlyText.cs                 every pattern, but only inside comments and string literals
  Quiet/notes.txt                   the same code in a file that is not .cs
```

The fixture is not in any project, so it is never compiled. The expected findings, with file, line and rule, are listed once in `tests/RepoScanner.Scanner.Tests/CodePatterns/FixturePatterns.cs`, as `FixtureSecrets` and `FixturePackages` do for the other fixtures. `Quiet/` gives no findings. Every rule has at least one finding and one case that is not a finding.

## README

- "Scan a local folder": the scan now checks code patterns too, and the rules, with their severities and references, are listed.
- "Known limitations" gains the limits below.

## Testing

Each rule is written test first: a failing test, then the code that makes it pass. Rule tests parse a short C# string and call `FindMatches` directly, so they need no files.

| Task | Tests |
|---|---|
| #49 | `SnippetHash`: the same code with other spacing, line breaks or comments gives the same hash; moving it down the file gives the same hash; the same code in another method or type gives a different one; nested types, a constructor, a property accessor, a lambda, a field and top-level statements give the parts in the table. `NestingDepth`: parentheses and angle brackets counted together, angle brackets starting again at `;`, `{` and `}`, stray closing brackets ignored. Scanner, with a stub rule: only `.cs` files, ignoring case; an unreadable file skipped and counted; a file nested 201 deep skipped and one nested 200 deep scanned; 100,000 nested parentheses skipped without crashing (without the limit, this test ends the test run with a stack overflow); a file the parser gives up on (CS8078) skipped with a warning; a file that throws `InsufficientExecutionStackException` skipped with a warning while the next is scanned; code that does not compile still scanned; code under `#if DEBUG` left out; line numbers from 1; the sort order; the summary line; `FingerprintCalculator` accepts every finding; the host resolves all three scanners |
| #50 | Each flagged form and each "does not flag" case under CP001, including the object initialiser and brackets around the SQL |
| #51 | Each row of the CP002 table, `TypeNameHandling.None`, a fully qualified type name, and the reference in each message |
| #52 | Both lambda forms, `+=`, `DangerousAcceptAnyServerCertificateValidator`, a callback with a check, and a named method |
| #53 | Each row of the CP004 table and SHA256; `AllowAnyOrigin`, `SetIsOriginAllowed` with and without a check, `WithOrigins`; an empty catch with a type, without one and with a filter; a comment inside; `throw;` |
| #54 | The whole scanner over the fixture gives exactly the findings in `FixturePatterns` (criterion 2); nothing from `Quiet/`; every rule appears at least once. The `--path` end-to-end test sees three `ScannerResult` rows |

- The 80% line coverage gate on the Scanner project covers the new classes.
- Manual check: run `--path` on the fixture and compare with `FixturePatterns`; then run it on a clone of a Blacklight repository and look through the findings for false alarms before the demo.

## Order of work

One task at a time, stopping after each for review, then a commit whose message starts "This commit" and ends in its task number (ADR-020):

1. **#48** This design document.
2. **#55** The ADR-018 amendment with the final rule set, to be confirmed with the technical manager while the rules are built.
3. **#49** The package, `ICodePatternRule`, `CodePatternMatch`, `CodePatternScanner`, `SnippetHash`, the helpers, the registration and the ADR-024 amendment.
4. **#50** `CP001`.
5. **#51** `CP002`.
6. **#52** `CP003`.
7. **#53** `CP004`, `CP005` and `CP006`.
8. **#54** The fixture, `FixturePatterns`, the fixture and end-to-end tests, and the README.

## Not in Issue 6

| Item | Where it is on the board |
|---|---|
| The queue loop | Issue 7 |
| Cloning, and the commit SHA on a scan run | Issue 8 |
| The rules left out in decision 4 | Not on the board: out of scope for a first release |
| Scanning `.razor` and `.cshtml` files | Not on the board: they need a parser other than Roslyn's C# one |
| Following a value through variables or into helper methods | Not on the board: needs Roslyn's semantic model and a build of each project (ADR-018) |
| A column for a rule's reference | Not needed: it is in the message (decision 6) |
| A `Scanner:CodePatterns` settings group | Not added: nothing reads it (ADR-022) |

## Known limitations

- Rules look at the shape of the code, not at what it means (ADR-018). A pattern behind a variable or a helper method is missed: SQL built in one line and passed to `FromSqlRaw` in the next, or a certificate callback set to a named method.
- Names are matched as written, without knowing their types. A class of the repository's own called `MD5` or `SqlCommand` is treated like the real one, and a `"SELECT " + SomeConstant` is flagged although the constant is safe. Such a finding is suppressed with a reason.
- `new SqlCommand(sql)` written as `new(sql)` is not seen, because the rule cannot tell which type `new()` makes.
- Files are parsed with no preprocessor symbols, so code inside `#if DEBUG` or another condition that a plain build leaves out is not scanned.
- A file that nests parentheses and angle brackets more than 200 deep is not scanned at all, and nor is one the parser gives up on. The count includes brackets in comments and strings, so a file with that many in a comment is skipped too. Each is logged as a warning.
- `.razor` and `.cshtml` files are not scanned; their `.razor.cs` code-behind files are.
- Weak hashing is flagged wherever it is used, including checksums, where it is harmless.
- Renaming a method, type or file, or changing the matched code, makes its findings new, and suppressions do not follow (ADR-024).
