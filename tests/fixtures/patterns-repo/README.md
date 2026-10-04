# patterns-repo fixture

A small, pretend repository for RepoScanner's code-pattern scanner tests and demo. The C# files are never built, and hold insecure code on purpose.

Each finding is marked with a comment naming its rule, such as `// CP001`, and next to each is a case the rule must not report.

| Folder | Rule | Findings | Not findings |
|---|---|---|---|
| `Data/` | CP001 SQL built from strings | 6: `+`, `$"{hole}"`, `string.Format`, `new SqlCommand(...)`, `CommandText` in an initialiser, a bare `Format(...)` after `using static System.String` | `FromSql`, a join of literals, a `@name` parameter, SQL in a variable, a class's own `Format` method |
| `Serialization/` | CP002 Insecure deserialization | 6: `BinaryFormatter`, `LosFormatter`, `NetDataContractSerializer`, `ObjectStateFormatter`, `TypeNameHandling.Auto` in settings, `TypeNameHandling.All` in an attribute | `TypeNameHandling.None` |
| `Http/` | CP003 Certificate validation switched off | 3: `=> true`, `DangerousAcceptAnyServerCertificateValidator`, `+= delegate { return true; }` | A callback that checks the errors |
| `Crypto/` | CP004 Weak hashing | 3: `MD5.Create()`, `SHA1.HashData(...)`, `new TripleDESCryptoServiceProvider()` | `SHA256.Create()` |
| `Web/` | CP005 Any origin allowed | 2: `AllowAnyOrigin()`, `SetIsOriginAllowed(origin => true)` | `WithOrigins("https://...")` |
| `Errors/` | CP006 Empty catch | 2: two empty catches in different methods | A catch with a comment inside, `catch { throw; }` |
| `Quiet/` | none | 0: every pattern, but only in comments, strings and a `.txt` file | |

That is 22 findings in all. The markers sit outside the code they mark, so they are comments the rules ignore; in `Errors/` they sit outside the braces, where a comment does not count as a reason for a catch being empty.

Scan it with:

    dotnet run --project src/RepoScanner.Scanner -- --path tests/fixtures/patterns-repo

The `.reposcanner-exclude` file stops these patterns being reported when the whole RepoScanner repository is scanned.
The findings the scanner must report are in `tests/RepoScanner.Scanner.Tests/CodePatterns/FixturePatterns.cs`.
