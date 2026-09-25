# Issue 3: Secret scanner against a local path, implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `dotnet run --project src/RepoScanner.Scanner -- --path <folder>` finds hard-coded secrets (SEC001 to SEC007) in a local folder and saves them to SQL as findings with masked previews and hashes, never the raw value.

**Architecture:** Core gains the `IScanner` contract and `RawFinding`. The Scanner project gains a shared `RepositoryFileWalker` (the skip rules), seven regex rule classes behind `ISecretRule`, a placeholder allow-list, a masker and a hasher, and `SecretScanner`, which ties them together. `LocalScanRunner` runs the scanners on a folder and saves new findings through the Issue 2 repositories, using an interim fingerprint until Issue 4. `Program` reads `--path` from the arguments and picks local mode or queue mode.

**Tech Stack:** .NET 10 (SDK 10.0.401), Generic Host, EF Core 10.0.12 (SQL Server; SQLite in tests), Serilog, NUnit 4.6 with NUnit.Analyzers, `Microsoft.Extensions.Diagnostics.Testing` (`FakeLogger`), coverlet.msbuild.

**Spec:** [docs/design/2026-09-25-secret-scanner-local-path.md](../../design/2026-09-25-secret-scanner-local-path.md). Read it before starting; this plan argues from it.

## Global Constraints

- Branch: `feature/14-secret-scanner-against-a-local-path` (already created from `develop`).
- **Commits:** one per task, only after the user has reviewed that task's diff. The message starts `This commit` and ends with the task number, for example `#15`. It never mentions Claude, Opus or any AI tool: no `Co-Authored-By` line.
- ADR-021 style, enforced by the build: explicit `using` directives (System first, then alphabetical), block-scoped namespaces, explicit types (no `var`), no top-level statements, `TreatWarningsAsErrors`. The build must finish with zero warnings. Every non-obvious line has a comment giving the reason, in British English.
- Central Package Management: a package version goes only in `Directory.Packages.props`; a `.csproj` lists `<PackageReference Include="..." />` without a version.
- No em dash (U+2014) in any file; use a hyphen, a colon or a new sentence.
- Rule IDs, severities and messages exactly as in the spec's Secret rules table: SEC001 Critical "Private key"; SEC002 Critical "Azure Storage or Service Bus key"; SEC003 High "Password in a connection string"; SEC004 High "Credentials in a URL"; SEC005 High "Entra ID client secret"; SEC006 High "NuGet API key"; SEC007 Medium "Password, secret or key in a named setting".
- Every rule pattern is built by `SecretRulePattern.Create`: `new Regex(pattern, RegexOptions.CultureInvariant | options, TimeSpan.FromSeconds(1))`, and has a group named `secret`.
- Secret values have all whitespace removed (`SecretValue.FromMatch`) before the allow-list, the masker and the hasher see them.
- Skip rules: folders `.git`, `bin`, `obj`, `node_modules`, `packages`, `.vs`, `TestResults` (names ignoring case); files larger than 1,048,576 bytes; a zero byte in the first 8,000 bytes; symbolic links; folders below the root that contain `.reposcanner-exclude`; files that cannot be opened.
- Mask: the first `min(4, length / 4)` characters, then `****`. Hash: SHA-256 of the UTF-8 bytes, 64 lower-case hex characters.
- Never log matched text or previews. Leak checks use `Assert.That(leaked, Is.False, "<rule> value leaked ...")`, never `Does.Not.Contain(value)`, whose failure message would print the value.
- Every test file that contains a fake secret lives under `tests/RepoScanner.Scanner.Tests/Secrets/`.
- Tests: NUnit 4, `Method_Condition_Result` names, the constraint model (`Assert.That`), `using (Assert.EnterMultipleScope())` for related assertions, and a comment above a test when its reason is not obvious.
- **Filtered test runs add `-p:CollectCoverage=false`**, because the 80% coverage gate counts only the tests that ran. The full `dotnet test RepoScanner.sln` at the end of each task checks coverage (Core and Scanner at 80% or more).

## Review Focus

Inputs the spec does not mention, most likely to bite a person first. Each has a test in the task named.

1. **Files saved with Windows line endings (CRLF):** line numbers and hashes must match the LF version of the same file. Git on Windows usually checks files out as CRLF. Test in Task 7: `ScanAsync_WithWindowsLineEndings_ReportsTheSameLinesAndHashes`.
2. **A file another program has locked**, which is common in a working folder: it is skipped with a warning naming it, and the rest of the folder is scanned. Test in Task 6: `GetFiles_SkipsAFileItCannotOpenAndLogsAWarningNamingIt`.
3. **A file that is not valid UTF-8**, such as an old Windows-1252 file with a `£` in it: it is still scanned. Test in Task 7: `ScanAsync_ScansAFileThatIsNotValidUtf8`.
4. **The same folder written differently** (a trailing separator, a relative path, different letter case): it is the same `Repositories` row. Test in Task 10: `DescribeFolder_GivesTheSameIdHoweverTheFolderIsWritten`.
5. **`--path` naming a file instead of a folder:** exit code 2 and one log line, not a crash. Test in Task 11: `Path_ToAFile_ExitsTwoWithOneLine`.

---

### Task 1: Design document and SQL Server version correction (#14)

**Files:**
- Commit (already written): `docs/design/2026-09-25-secret-scanner-local-path.md`
- Modify: `docs/adr/ADR-017-everything-runs-locally-before-azure.md:20`
- Modify: `docs/design/2026-09-24-data-model-and-persistence.md:37`

**Interfaces:** none.

- [ ] **Step 1: Confirm the local SQL Server version**

Run (PowerShell):
```powershell
(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL').SQLEXPRESS
```
Expected: `MSSQL15.SQLEXPRESS`. Version 15 is SQL Server 2019.

- [ ] **Step 2: Correct the ADR-017 amendment**

In `docs/adr/ADR-017-everything-runs-locally-before-azure.md` line 20, replace `(SQL Server 2022 Express, `.\SQLEXPRESS`)` with `(SQL Server 2019 Express, `.\SQLEXPRESS`)`. Change nothing else: the decision is unchanged, only the fact is corrected.

- [ ] **Step 3: Correct the Issue 2 design**

In `docs/design/2026-09-24-data-model-and-persistence.md` line 37, replace `the SQL Server 2022 Express instance` with `the SQL Server 2019 Express instance`.

- [ ] **Step 4: Check nothing else says 2022**

Run: `git grep -n "2022 Express"`
Expected: no output.

- [ ] **Step 5: Stop for review, then commit**

Show the user `git status` and `git diff`, and wait for their approval. Then:
```bash
git add docs/design/2026-09-25-secret-scanner-local-path.md docs/adr/ADR-017-everything-runs-locally-before-azure.md docs/design/2026-09-24-data-model-and-persistence.md
git commit -m "This commit adds the Issue 3 design document and corrects the local SQL Server version to 2019 #14"
```

---

### Task 2: IScanner and RawFinding in Core (#15)

**Files:**
- Create: `src/RepoScanner.Core/Scanning/IScanner.cs`
- Create: `src/RepoScanner.Core/Scanning/RawFinding.cs`
- Test: `tests/RepoScanner.Core.Tests/Scanning/RawFindingTests.cs`

**Interfaces:**
- Consumes: `RepoScanner.Core.Enums.ScannerType`, `RepoScanner.Core.Enums.Severity`, `RepoScanner.Core.Entities.Finding` (Issue 2).
- Produces:
  - `namespace RepoScanner.Core.Scanning`
  - `public interface IScanner { ScannerType ScannerType { get; } Task<IReadOnlyList<RawFinding>> ScanAsync(string rootPath, CancellationToken cancellationToken); }`
  - `public sealed record RawFinding` with init-only properties: `required ScannerType ScannerType`, `required string RuleId`, `required Severity Severity`, `required string FilePath`, `int? LineNumber`, `required string Message`, `string? MaskedPreview`, `string? SecretHash`, `string? PackageName`, `string? PackageVersion`, `string? AdvisoryUrl`.

- [ ] **Step 1: Write the failing test**

`tests/RepoScanner.Core.Tests/Scanning/RawFindingTests.cs`:
```csharp
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using RepoScanner.Core.Entities;
using RepoScanner.Core.Scanning;

namespace RepoScanner.Core.Tests.Scanning
{
    [TestFixture]
    public class RawFindingTests
    {
        // Issue 3 design: a raw finding carries what a scanner knows and nothing else. In particular
        // there is no property that could hold a secret's raw value, so one cannot be saved by mistake.
        [Test]
        public void RawFinding_HasOnlyTheFindingColumnsAScannerCanKnow()
        {
            string[] names = typeof(RawFinding)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => property.Name)
                .ToArray();

            Assert.That(names, Is.EquivalentTo(new string[]
            {
                "ScannerType", "RuleId", "Severity", "FilePath", "LineNumber", "Message",
                "MaskedPreview", "SecretHash", "PackageName", "PackageVersion", "AdvisoryUrl"
            }));
        }

        // Each property is copied onto a Finding of the same name when it is saved
        [Test]
        public void RawFinding_EachPropertyMatchesAFindingColumnOfTheSameType()
        {
            foreach (PropertyInfo property in typeof(RawFinding).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                PropertyInfo? column = typeof(Finding).GetProperty(property.Name);

                Assert.That(column?.PropertyType, Is.EqualTo(property.PropertyType), property.Name);
            }
        }
    }
}
```

- [ ] **Step 2: Run it to make sure it fails**

Run: `dotnet test tests/RepoScanner.Core.Tests --filter "FullyQualifiedName~RawFindingTests" -p:CollectCoverage=false`
Expected: build error `CS0234: The type or namespace name 'Scanning' does not exist in the namespace 'RepoScanner.Core'`.

- [ ] **Step 3: Write the contract**

`src/RepoScanner.Core/Scanning/IScanner.cs`:
```csharp
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RepoScanner.Core.Enums;

namespace RepoScanner.Core.Scanning
{
    // One of the three scanners in the one scanner image (ADR-009): secrets, packages and code patterns.
    // The contract lives in Core so the orchestrator can run every scanner the same way.
    public interface IScanner
    {
        ScannerType ScannerType { get; }

        // Scans the files under rootPath and returns what it found. Never touches the database: saving
        // is the caller's job, once the findings have been matched against the stored ones.
        Task<IReadOnlyList<RawFinding>> ScanAsync(string rootPath, CancellationToken cancellationToken);
    }
}
```

`src/RepoScanner.Core/Scanning/RawFinding.cs`:
```csharp
using RepoScanner.Core.Enums;

namespace RepoScanner.Core.Scanning
{
    // What a scanner reports about one problem, before it is matched against the stored findings. It
    // holds exactly the Finding columns a scanner can know. There is deliberately no property for a
    // secret's raw value, so one can never be passed on or saved by mistake (Issue 3 design).
    public sealed record RawFinding
    {
        public required ScannerType ScannerType { get; init; }

        // SEC001, PKG001, CP001 and so on
        public required string RuleId { get; init; }

        public required Severity Severity { get; init; }

        // Relative to the scanned folder, with / between folders
        public required string FilePath { get; init; }

        // Counted from 1. For display only: not part of the fingerprint (#27)
        public int? LineNumber { get; init; }

        public required string Message { get; init; }

        // Secret findings only
        public string? MaskedPreview { get; init; }

        // Secret findings only: SHA-256 of the secret in hex (ADR-023)
        public string? SecretHash { get; init; }

        // Package findings only (Issue 7)
        public string? PackageName { get; init; }

        public string? PackageVersion { get; init; }

        public string? AdvisoryUrl { get; init; }
    }
}
```

- [ ] **Step 4: Run the test to make sure it passes**

Run: `dotnet test tests/RepoScanner.Core.Tests --filter "FullyQualifiedName~RawFindingTests" -p:CollectCoverage=false`
Expected: `Passed! - Failed: 0, Passed: 2`.

- [ ] **Step 5: Run everything**

Run: `dotnet test RepoScanner.sln`
Expected: every project `Passed!`, no coverage failure.

- [ ] **Step 6: Stop for review, then commit**

Show the user the diff and wait for approval. Then:
```bash
git add src/RepoScanner.Core/Scanning tests/RepoScanner.Core.Tests/Scanning
git commit -m "This commit adds IScanner and RawFinding to Core, the contract every scanner implements #15"
```

---

### Task 3: ISecretRule and the seven rules (#17)

**Files:**
- Create: `src/RepoScanner.Scanner/Secrets/ISecretRule.cs`
- Create: `src/RepoScanner.Scanner/Secrets/SecretRulePattern.cs`
- Create: `src/RepoScanner.Scanner/Secrets/SecretValue.cs`
- Create: `src/RepoScanner.Scanner/Secrets/Rules/Sec001PrivateKeyRule.cs` to `Sec007NamedSettingRule.cs` (seven files)
- Test: `tests/RepoScanner.Scanner.Tests/Secrets/RuleMatches.cs`
- Test: `tests/RepoScanner.Scanner.Tests/Secrets/AllSecretRules.cs`
- Test: `tests/RepoScanner.Scanner.Tests/Secrets/SecretValueTests.cs`
- Test: `tests/RepoScanner.Scanner.Tests/Secrets/Rules/Sec001PrivateKeyRuleTests.cs` to `Sec007NamedSettingRuleTests.cs`, and `AllRulesTests.cs`

**Interfaces:**
- Consumes: `RepoScanner.Core.Enums.Severity`.
- Produces (all `namespace RepoScanner.Scanner.Secrets` unless stated):
  - `public interface ISecretRule { string RuleId { get; } Severity Severity { get; } string Message { get; } Regex Pattern { get; } }`
  - `public static class SecretRulePattern { public static readonly TimeSpan MatchTimeout; public static Regex Create(string pattern, RegexOptions options = RegexOptions.None); }`
  - `public static class SecretValue { public const string GroupName = "secret"; public static string FromMatch(Match match); }`
  - `namespace RepoScanner.Scanner.Secrets.Rules`: `Sec001PrivateKeyRule`, `Sec002AzureSharedKeyRule`, `Sec003ConnectionStringPasswordRule`, `Sec004UrlCredentialsRule`, `Sec005EntraClientSecretRule`, `Sec006NuGetApiKeyRule`, `Sec007NamedSettingRule`: each `public sealed class ... : ISecretRule` with a parameterless constructor.
  - Test helpers (`namespace RepoScanner.Scanner.Tests.Secrets`, `internal static`): `RuleMatches.SecretsFoundBy(ISecretRule rule, string text) : IReadOnlyList<string>`; `AllSecretRules.Create() : ISecretRule[]`.

- [ ] **Step 1: Write the failing test for the shared pieces**

`tests/RepoScanner.Scanner.Tests/Secrets/SecretValueTests.cs`:
```csharp
using System;
using System.Text.RegularExpressions;
using NUnit.Framework;
using RepoScanner.Scanner.Secrets;

namespace RepoScanner.Scanner.Tests.Secrets
{
    [TestFixture]
    public class SecretValueTests
    {
        // Line endings and spaces never change the secret, so they never change its hash
        [Test]
        public void FromMatch_ReturnsTheSecretGroupWithoutWhitespace()
        {
            Regex pattern = SecretRulePattern.Create(@"key=(?<secret>[a-z\s]+);");
            Match match = pattern.Match("key=ab c\r\nd ;");

            Assert.That(SecretValue.FromMatch(match), Is.EqualTo("abcd"));
        }

        [Test]
        public void Create_UsesTheOneSecondTimeoutAndCultureInvariantMatching()
        {
            Regex pattern = SecretRulePattern.Create("a", RegexOptions.IgnoreCase);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pattern.MatchTimeout, Is.EqualTo(TimeSpan.FromSeconds(1)));
                Assert.That(pattern.Options, Is.EqualTo(RegexOptions.CultureInvariant | RegexOptions.IgnoreCase));
            }
        }
    }
}
```

`tests/RepoScanner.Scanner.Tests/Secrets/RuleMatches.cs`:
```csharp
using System.Collections.Generic;
using System.Linq;
using RepoScanner.Scanner.Secrets;

namespace RepoScanner.Scanner.Tests.Secrets
{
    // What a rule finds in some text, read the way SecretScanner reads it
    internal static class RuleMatches
    {
        public static IReadOnlyList<string> SecretsFoundBy(ISecretRule rule, string text)
        {
            return rule.Pattern.Matches(text).Select(SecretValue.FromMatch).ToList();
        }
    }
}
```

- [ ] **Step 2: Run it to make sure it fails**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~SecretValueTests" -p:CollectCoverage=false`
Expected: build error `CS0234: The type or namespace name 'Secrets' does not exist in the namespace 'RepoScanner.Scanner'`.

- [ ] **Step 3: Write the shared pieces**

`src/RepoScanner.Scanner/Secrets/ISecretRule.cs`:
```csharp
using System.Text.RegularExpressions;
using RepoScanner.Core.Enums;

namespace RepoScanner.Scanner.Secrets
{
    // One kind of hard-coded secret, SEC001 to SEC007 (Issue 3 design, Secret rules). SecretScanner
    // runs every rule's Pattern over the whole text of each file.
    public interface ISecretRule
    {
        // SEC001 and so on. Also the order the rules run in, so the more specific rule wins an overlap.
        string RuleId { get; }

        Severity Severity { get; }

        // Shown on the dashboard, for example "Private key"
        string Message { get; }

        // Contains a group named "secret" (SecretValue.GroupName): the part that is masked and hashed.
        // Built with SecretRulePattern.Create, so it has the one-second match timeout.
        Regex Pattern { get; }
    }
}
```

`src/RepoScanner.Scanner/Secrets/SecretRulePattern.cs`:
```csharp
using System;
using System.Text.RegularExpressions;

namespace RepoScanner.Scanner.Secrets
{
    // Builds every rule's regular expression the same way. The timeout stops a file written to make a
    // pattern backtrack for ever from hanging the scan; SecretScanner catches it and moves on. Built
    // with new Regex rather than [GeneratedRegex]: ADR-021 prefers code that is visible in the source
    // over code a compiler feature adds.
    public static class SecretRulePattern
    {
        public static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

        public static Regex Create(string pattern, RegexOptions options = RegexOptions.None)
        {
            // CultureInvariant: matching that ignores case must not depend on the machine's culture
            // (in Turkish, "I" and "i" are different letters)
            return new Regex(pattern, RegexOptions.CultureInvariant | options, MatchTimeout);
        }
    }
}
```

`src/RepoScanner.Scanner/Secrets/SecretValue.cs`:
```csharp
using System.Linq;
using System.Text.RegularExpressions;

namespace RepoScanner.Scanner.Secrets
{
    // The secret a rule matched: its "secret" group with all whitespace removed. Only SEC001 (a key
    // split over several lines) and SEC003 (spaces around a password) can contain any. Removing it
    // means Windows and Unix line endings give the same secret, and so the same hash.
    public static class SecretValue
    {
        public const string GroupName = "secret";

        public static string FromMatch(Match match)
        {
            string value = match.Groups[GroupName].Value;
            return string.Concat(value.Where(character => !char.IsWhiteSpace(character)));
        }
    }
}
```

- [ ] **Step 4: Run it to make sure it passes**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~SecretValueTests" -p:CollectCoverage=false`
Expected: `Passed: 2`.

- [ ] **Step 5: Write the failing SEC001 test**

`tests/RepoScanner.Scanner.Tests/Secrets/Rules/Sec001PrivateKeyRuleTests.cs`:
```csharp
using System.Collections.Generic;
using NUnit.Framework;
using RepoScanner.Core.Enums;
using RepoScanner.Scanner.Secrets.Rules;

namespace RepoScanner.Scanner.Tests.Secrets.Rules
{
    [TestFixture]
    public class Sec001PrivateKeyRuleTests
    {
        // Two lines of a fake key. The rule reports them joined, with the line break removed.
        private const string FirstLine = "MIIEFAKEprivateKeyForRepoScannerTestsAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        private const string SecondLine = "FAKEsecondLineOfTheKeyBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

        private readonly Sec001PrivateKeyRule _rule = new Sec001PrivateKeyRule();

        [Test]
        public void Rule_IsACriticalPrivateKey()
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_rule.RuleId, Is.EqualTo("SEC001"));
                Assert.That(_rule.Severity, Is.EqualTo(Severity.Critical));
                Assert.That(_rule.Message, Is.EqualTo("Private key"));
            }
        }

        [TestCase("")]
        [TestCase("RSA ")]
        [TestCase("EC ")]
        [TestCase("DSA ")]
        [TestCase("OPENSSH ")]
        [TestCase("ENCRYPTED ")]
        public void Pattern_FindsEachKindOfPrivateKey(string type)
        {
            IReadOnlyList<string> secrets = RuleMatches.SecretsFoundBy(_rule, Pem(type, "\n"));

            Assert.That(secrets, Is.EqualTo(new string[] { FirstLine + SecondLine }));
        }

        // Whitespace is removed, so a key saved with Windows line endings is the same secret
        [Test]
        public void Pattern_WithWindowsLineEndings_FindsTheSameSecret()
        {
            IReadOnlyList<string> secrets = RuleMatches.SecretsFoundBy(_rule, Pem("RSA ", "\r\n"));

            Assert.That(secrets, Is.EqualTo(new string[] { FirstLine + SecondLine }));
        }

        // An older password-protected RSA key has header lines inside the block
        [Test]
        public void Pattern_FindsAKeyWithEncryptionHeaders()
        {
            string pem = "-----BEGIN RSA PRIVATE KEY-----\nProc-Type: 4,ENCRYPTED\nDEK-Info: AES-128-CBC,FAKE0123456789ABCDEF\n\n"
                + FirstLine + "\n-----END RSA PRIVATE KEY-----\n";

            Assert.That(RuleMatches.SecretsFoundBy(_rule, pem), Has.Count.EqualTo(1));
        }

        [Test]
        public void Pattern_IgnoresAPublicKey()
        {
            string pem = "-----BEGIN PUBLIC KEY-----\n" + FirstLine + "\n-----END PUBLIC KEY-----\n";

            Assert.That(RuleMatches.SecretsFoundBy(_rule, pem), Is.Empty);
        }

        [Test]
        public void Pattern_IgnoresABlockWhoseEndDoesNotMatchItsBeginning()
        {
            string pem = "-----BEGIN RSA PRIVATE KEY-----\n" + FirstLine + "\n-----END EC PRIVATE KEY-----\n";

            Assert.That(RuleMatches.SecretsFoundBy(_rule, pem), Is.Empty);
        }

        private static string Pem(string type, string newLine)
        {
            return "-----BEGIN " + type + "PRIVATE KEY-----" + newLine
                + FirstLine + newLine
                + SecondLine + newLine
                + "-----END " + type + "PRIVATE KEY-----" + newLine;
        }
    }
}
```

- [ ] **Step 6: Run it to make sure it fails**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~Sec001" -p:CollectCoverage=false`
Expected: build error `CS0234: The type or namespace name 'Rules' does not exist in the namespace 'RepoScanner.Scanner.Secrets'`.

- [ ] **Step 7: Write SEC001**

`src/RepoScanner.Scanner/Secrets/Rules/Sec001PrivateKeyRule.cs`:
```csharp
using System.Text.RegularExpressions;
using RepoScanner.Core.Enums;

namespace RepoScanner.Scanner.Secrets.Rules
{
    // A PEM private key block: RSA, EC, DSA, OpenSSH or PKCS#8, encrypted or not. Whoever holds it can
    // sign or decrypt as its owner, so it is Critical (Issue 3 design, Secret rules).
    public sealed class Sec001PrivateKeyRule : ISecretRule
    {
        public string RuleId => "SEC001";

        public Severity Severity => Severity.Critical;

        public string Message => "Private key";

        // From BEGIN to the END line of the same type (\k<type>), lazily, so two keys in one file are
        // two matches. The secret is everything in between: base64, line breaks and, in an older
        // encrypted key, header lines such as "Proc-Type: 4,ENCRYPTED".
        public Regex Pattern { get; } = SecretRulePattern.Create(
            @"-----BEGIN (?<type>(?:RSA |EC |DSA |OPENSSH |ENCRYPTED )?)PRIVATE KEY-----" +
            @"(?<secret>[A-Za-z0-9+/=\s:,-]+?)" +
            @"-----END \k<type>PRIVATE KEY-----");
    }
}
```

- [ ] **Step 8: Run it to make sure it passes**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~Sec001" -p:CollectCoverage=false`
Expected: `Passed: 11`.

- [ ] **Step 9: Write the failing SEC002 test**

`tests/RepoScanner.Scanner.Tests/Secrets/Rules/Sec002AzureSharedKeyRuleTests.cs`:
```csharp
using NUnit.Framework;
using RepoScanner.Core.Enums;
using RepoScanner.Scanner.Secrets.Rules;

namespace RepoScanner.Scanner.Tests.Secrets.Rules
{
    [TestFixture]
    public class Sec002AzureSharedKeyRuleTests
    {
        // A storage account key is 88 base64 characters; a Service Bus key is 44
        private const string StorageKey = "FAKEstorageAccountKeyForRepoScannerTests0000000000000000000000000000000000000000000000==";
        private const string ServiceBusKey = "FAKEserviceBusKeyForRepoScannerTests0000000=";

        private readonly Sec002AzureSharedKeyRule _rule = new Sec002AzureSharedKeyRule();

        [Test]
        public void Rule_IsACriticalAzureSharedKey()
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_rule.RuleId, Is.EqualTo("SEC002"));
                Assert.That(_rule.Severity, Is.EqualTo(Severity.Critical));
                Assert.That(_rule.Message, Is.EqualTo("Azure Storage or Service Bus key"));
            }
        }

        [Test]
        public void Pattern_FindsTheKeyInAStorageConnectionString()
        {
            string text = "DefaultEndpointsProtocol=https;AccountName=fakeaccount;AccountKey=" + StorageKey + ";EndpointSuffix=core.windows.net";

            Assert.That(RuleMatches.SecretsFoundBy(_rule, text), Is.EqualTo(new string[] { StorageKey }));
        }

        [Test]
        public void Pattern_FindsTheKeyInAServiceBusConnectionString()
        {
            string text = "Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=" + ServiceBusKey;

            Assert.That(RuleMatches.SecretsFoundBy(_rule, text), Is.EqualTo(new string[] { ServiceBusKey }));
        }

        // Azure reads connection string keys in any case
        [Test]
        public void Pattern_IgnoresTheCaseOfTheKeyName()
        {
            Assert.That(RuleMatches.SecretsFoundBy(_rule, "accountkey=" + StorageKey), Is.EqualTo(new string[] { StorageKey }));
        }

        [TestCase("AccountKey=abc123;")]
        [TestCase("AccountKey=<your-storage-key>;")]
        [TestCase("SharedAccessKeyName=RootManageSharedAccessKey;")]
        public void Pattern_IgnoresLookAlikes(string text)
        {
            Assert.That(RuleMatches.SecretsFoundBy(_rule, text), Is.Empty);
        }
    }
}
```

- [ ] **Step 10: Run it to make sure it fails**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~Sec002" -p:CollectCoverage=false`
Expected: build error `CS0246: The type or namespace name 'Sec002AzureSharedKeyRule' could not be found`.

- [ ] **Step 11: Write SEC002**

`src/RepoScanner.Scanner/Secrets/Rules/Sec002AzureSharedKeyRule.cs`:
```csharp
using System.Text.RegularExpressions;
using RepoScanner.Core.Enums;

namespace RepoScanner.Scanner.Secrets.Rules
{
    // The shared key in an Azure Storage or Service Bus connection string. It gives full access to the
    // account or namespace, so it is Critical (Issue 3 design, Secret rules).
    public sealed class Sec002AzureSharedKeyRule : ISecretRule
    {
        public string RuleId => "SEC002";

        public Severity Severity => Severity.Critical;

        public string Message => "Azure Storage or Service Bus key";

        // At least 40 base64 characters: storage keys are 88 and Service Bus keys 44, while a
        // placeholder such as <your-storage-key> is not base64. "SharedAccessKeyName=" is not matched,
        // because "=" must follow the key name directly.
        public Regex Pattern { get; } = SecretRulePattern.Create(
            @"\b(?:AccountKey|SharedAccessKey)\s*=\s*(?<secret>[A-Za-z0-9+/]{40,}={0,2})",
            RegexOptions.IgnoreCase);
    }
}
```

- [ ] **Step 12: Run it to make sure it passes**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~Sec002" -p:CollectCoverage=false`
Expected: `Passed: 7`.

- [ ] **Step 13: Write the failing SEC003 test**

`tests/RepoScanner.Scanner.Tests/Secrets/Rules/Sec003ConnectionStringPasswordRuleTests.cs`:
```csharp
using NUnit.Framework;
using RepoScanner.Core.Enums;
using RepoScanner.Scanner.Secrets.Rules;

namespace RepoScanner.Scanner.Tests.Secrets.Rules
{
    [TestFixture]
    public class Sec003ConnectionStringPasswordRuleTests
    {
        private readonly Sec003ConnectionStringPasswordRule _rule = new Sec003ConnectionStringPasswordRule();

        [Test]
        public void Rule_IsAHighConnectionStringPassword()
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_rule.RuleId, Is.EqualTo("SEC003"));
                Assert.That(_rule.Severity, Is.EqualTo(Severity.High));
                Assert.That(_rule.Message, Is.EqualTo("Password in a connection string"));
            }
        }

        [TestCase("\"Server=tcp:orders-FAKE.database.windows.net,1433;Database=Orders;User ID=orders_app;Password=FAKE-Orders-Pa55word;Encrypt=True\"", "FAKE-Orders-Pa55word")]
        [TestCase("Pwd = FAKE-pwd-first ;Uid=app;Data Source=db01", "FAKE-pwd-first")]
        [TestCase("Host=db;Username=app;Password=FAKEpostgres", "FAKEpostgres")]
        [TestCase("Data Source=db01;Initial Catalog=Orders;password=FAKE-lower-case", "FAKE-lower-case")]
        public void Pattern_FindsThePassword(string text, string expected)
        {
            Assert.That(RuleMatches.SecretsFoundBy(_rule, text), Is.EqualTo(new string[] { expected }));
        }

        [TestCase("Server=(localdb)\\mssqllocaldb;Database=Orders;Trusted_Connection=True")]
        [TestCase("Password=FAKE-no-server;Timeout=30")]
        [TestCase("Server=db01;OldPassword=FAKE-old")]
        [TestCase("\"Server=db01\", \"Password=FAKE-other-string;\"")]
        [TestCase("Server=db01;Password=;")]
        public void Pattern_IgnoresLookAlikes(string text)
        {
            Assert.That(RuleMatches.SecretsFoundBy(_rule, text), Is.Empty);
        }
    }
}
```

- [ ] **Step 14: Run it to make sure it fails**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~Sec003" -p:CollectCoverage=false`
Expected: build error `CS0246: The type or namespace name 'Sec003ConnectionStringPasswordRule' could not be found`.

- [ ] **Step 15: Write SEC003**

`src/RepoScanner.Scanner/Secrets/Rules/Sec003ConnectionStringPasswordRule.cs`:
```csharp
using System.Text.RegularExpressions;
using RepoScanner.Core.Enums;

namespace RepoScanner.Scanner.Secrets.Rules
{
    // A password in a database connection string (SQL Server, PostgreSQL, MySQL and so on). It is
    // exactly what ADR-007 says this tool must flag, and it is High (Issue 3 design, Secret rules).
    public sealed class Sec003ConnectionStringPasswordRule : ISecretRule
    {
        // One key=value; pair. Quotes are excluded, so a match stays inside one string literal.
        private const string Pair = @"[^;'""\r\n]*;";

        // A key that says this is a connection string to a server
        private const string ServerKey = @"(?:Server|Data\s*Source|Host|Address)\s*=";

        // The password is the value, up to ; or a quote or the end of the line
        private const string PasswordKey = @"(?:Password|Pwd)\s*=\s*(?<secret>[^;'""\r\n]+)";

        // The server usually comes first; the password can come first too
        private const string ServerFirst = @"\b" + ServerKey + Pair + "(?:" + Pair + @")*?\s*" + PasswordKey;
        private const string PasswordFirst = @"\b" + PasswordKey + ";(?:" + Pair + @")*?\s*" + ServerKey;

        public string RuleId => "SEC003";

        public Severity Severity => Severity.High;

        public string Message => "Password in a connection string";

        public Regex Pattern { get; } = SecretRulePattern.Create(ServerFirst + "|" + PasswordFirst, RegexOptions.IgnoreCase);
    }
}
```

- [ ] **Step 16: Run it to make sure it passes**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~Sec003" -p:CollectCoverage=false`
Expected: `Passed: 10`.

- [ ] **Step 17: Write the failing SEC004 test**

`tests/RepoScanner.Scanner.Tests/Secrets/Rules/Sec004UrlCredentialsRuleTests.cs`:
```csharp
using NUnit.Framework;
using RepoScanner.Core.Enums;
using RepoScanner.Scanner.Secrets.Rules;

namespace RepoScanner.Scanner.Tests.Secrets.Rules
{
    [TestFixture]
    public class Sec004UrlCredentialsRuleTests
    {
        private readonly Sec004UrlCredentialsRule _rule = new Sec004UrlCredentialsRule();

        [Test]
        public void Rule_IsHighCredentialsInAUrl()
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_rule.RuleId, Is.EqualTo("SEC004"));
                Assert.That(_rule.Severity, Is.EqualTo(Severity.High));
                Assert.That(_rule.Message, Is.EqualTo("Credentials in a URL"));
            }
        }

        [TestCase("$feed = \"https://build-agent:FAKE-feed-pa55@pkgs.example.com/nuget/v3/index.json\"", "FAKE-feed-pa55")]
        [TestCase("mongodb+srv://app:FAKEmongo@cluster0.example.net/orders", "FAKEmongo")]
        [TestCase("git clone http://ci:FAKE-git-pass@git.example.com/repo.git", "FAKE-git-pass")]
        public void Pattern_FindsThePassword(string text, string expected)
        {
            Assert.That(RuleMatches.SecretsFoundBy(_rule, text), Is.EqualTo(new string[] { expected }));
        }

        // ADR-016: the clone URL Azure DevOps offers has a user name only, which is not a secret
        [TestCase("https://blacklight@dev.azure.com/blacklight/Lumina/_git/RepoScanner")]
        [TestCase("http://localhost:5000/api")]
        [TestCase("https://example.com/path?user=a:b")]
        public void Pattern_IgnoresLookAlikes(string text)
        {
            Assert.That(RuleMatches.SecretsFoundBy(_rule, text), Is.Empty);
        }
    }
}
```

- [ ] **Step 18: Run it to make sure it fails**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~Sec004" -p:CollectCoverage=false`
Expected: build error `CS0246: The type or namespace name 'Sec004UrlCredentialsRule' could not be found`.

- [ ] **Step 19: Write SEC004**

`src/RepoScanner.Scanner/Secrets/Rules/Sec004UrlCredentialsRule.cs`:
```csharp
using System.Text.RegularExpressions;
using RepoScanner.Core.Enums;

namespace RepoScanner.Scanner.Secrets.Rules
{
    // A user name and password written into a URL, as in https://user:password@host. ADR-016 calls
    // this "exactly the pattern this tool flags". High (Issue 3 design, Secret rules).
    public sealed class Sec004UrlCredentialsRule : ISecretRule
    {
        public string RuleId => "SEC004";

        public Severity Severity => Severity.High;

        public string Message => "Credentials in a URL";

        // scheme://user:password@host. The user part cannot contain ":", so a URL with a user name only
        // (https://org@dev.azure.com) or a port (http://localhost:5000) is not matched.
        public Regex Pattern { get; } = SecretRulePattern.Create(
            @"\b[A-Za-z][A-Za-z0-9+.-]*://[^\s/:@'""]+:(?<secret>[^\s/@'""]+)@[A-Za-z0-9.-]+");
    }
}
```

- [ ] **Step 20: Run it to make sure it passes**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~Sec004" -p:CollectCoverage=false`
Expected: `Passed: 7`.

- [ ] **Step 21: Write the failing SEC005 test**

`tests/RepoScanner.Scanner.Tests/Secrets/Rules/Sec005EntraClientSecretRuleTests.cs`:
```csharp
using NUnit.Framework;
using RepoScanner.Core.Enums;
using RepoScanner.Scanner.Secrets.Rules;

namespace RepoScanner.Scanner.Tests.Secrets.Rules
{
    [TestFixture]
    public class Sec005EntraClientSecretRuleTests
    {
        // 3 characters, a digit, Q~, then 34 characters: 40 in all
        private const string ClientSecret = "FAK8Q~FAKE.client_secret~for-RepoScanner";

        private readonly Sec005EntraClientSecretRule _rule = new Sec005EntraClientSecretRule();

        [Test]
        public void Rule_IsAHighEntraClientSecret()
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_rule.RuleId, Is.EqualTo("SEC005"));
                Assert.That(_rule.Severity, Is.EqualTo(Severity.High));
                Assert.That(_rule.Message, Is.EqualTo("Entra ID client secret"));
            }
        }

        [Test]
        public void Pattern_FindsAClientSecretInCode()
        {
            string text = "public const string GraphClientSecret = \"" + ClientSecret + "\";";

            Assert.That(RuleMatches.SecretsFoundBy(_rule, text), Is.EqualTo(new string[] { ClientSecret }));
        }

        // 31 to 34 characters after Q~, and no more: a longer token is something else
        [TestCase(30)]
        [TestCase(35)]
        public void Pattern_IgnoresTheWrongLength(int length)
        {
            Assert.That(RuleMatches.SecretsFoundBy(_rule, "abc8Q~" + new string('x', length)), Is.Empty);
        }

        [Test]
        public void Pattern_IgnoresATokenWithoutQTilde()
        {
            Assert.That(RuleMatches.SecretsFoundBy(_rule, "abc8Qx" + new string('x', 34)), Is.Empty);
        }

        [Test]
        public void Pattern_IgnoresALowerCaseQ()
        {
            Assert.That(RuleMatches.SecretsFoundBy(_rule, "abc8q~" + new string('x', 34)), Is.Empty);
        }
    }
}
```

- [ ] **Step 22: Run it to make sure it fails**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~Sec005" -p:CollectCoverage=false`
Expected: build error `CS0246: The type or namespace name 'Sec005EntraClientSecretRule' could not be found`.

- [ ] **Step 23: Write SEC005**

`src/RepoScanner.Scanner/Secrets/Rules/Sec005EntraClientSecretRule.cs`:
```csharp
using System.Text.RegularExpressions;
using RepoScanner.Core.Enums;

namespace RepoScanner.Scanner.Secrets.Rules
{
    // An Entra ID (Azure AD) application's client secret, recognised by its format, which contains
    // "Q~". It lets anyone sign in as the application, so it is High (Issue 3 design, Secret rules).
    public sealed class Sec005EntraClientSecretRule : ISecretRule
    {
        public string RuleId => "SEC005";

        public Severity Severity => Severity.High;

        public string Message => "Entra ID client secret";

        // 3 characters, a digit, Q~, then 31 to 34 characters. The lookarounds make it a whole token,
        // not part of a longer one. Case-sensitive: the Q is always upper case.
        public Regex Pattern { get; } = SecretRulePattern.Create(
            @"(?<![A-Za-z0-9_~.-])(?<secret>[A-Za-z0-9_~.]{3}[0-9]Q~[A-Za-z0-9_~.-]{31,34})(?![A-Za-z0-9_~.-])");
    }
}
```

- [ ] **Step 24: Run it to make sure it passes**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~Sec005" -p:CollectCoverage=false`
Expected: `Passed: 6`.

- [ ] **Step 25: Write the failing SEC006 test**

`tests/RepoScanner.Scanner.Tests/Secrets/Rules/Sec006NuGetApiKeyRuleTests.cs`:
```csharp
using NUnit.Framework;
using RepoScanner.Core.Enums;
using RepoScanner.Scanner.Secrets.Rules;

namespace RepoScanner.Scanner.Tests.Secrets.Rules
{
    [TestFixture]
    public class Sec006NuGetApiKeyRuleTests
    {
        // oy2 and 43 lower-case letters and digits
        private const string ApiKey = "oy2fakenugetapikeyforreposcannertests000000000";

        private readonly Sec006NuGetApiKeyRule _rule = new Sec006NuGetApiKeyRule();

        [Test]
        public void Rule_IsAHighNuGetApiKey()
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_rule.RuleId, Is.EqualTo("SEC006"));
                Assert.That(_rule.Severity, Is.EqualTo(Severity.High));
                Assert.That(_rule.Message, Is.EqualTo("NuGet API key"));
            }
        }

        [Test]
        public void Pattern_FindsAKeyOnTheCommandLine()
        {
            string text = "dotnet nuget push ./out/*.nupkg --source $feed --api-key " + ApiKey;

            Assert.That(RuleMatches.SecretsFoundBy(_rule, text), Is.EqualTo(new string[] { ApiKey }));
        }

        [TestCase(42)]
        [TestCase(44)]
        public void Pattern_IgnoresTheWrongLength(int length)
        {
            Assert.That(RuleMatches.SecretsFoundBy(_rule, "oy2" + new string('a', length)), Is.Empty);
        }

        [Test]
        public void Pattern_IgnoresUpperCaseLetters()
        {
            Assert.That(RuleMatches.SecretsFoundBy(_rule, "oy2" + new string('A', 43)), Is.Empty);
        }
    }
}
```

- [ ] **Step 26: Run it to make sure it fails**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~Sec006" -p:CollectCoverage=false`
Expected: build error `CS0246: The type or namespace name 'Sec006NuGetApiKeyRule' could not be found`.

- [ ] **Step 27: Write SEC006**

`src/RepoScanner.Scanner/Secrets/Rules/Sec006NuGetApiKeyRule.cs`:
```csharp
using System.Text.RegularExpressions;
using RepoScanner.Core.Enums;

namespace RepoScanner.Scanner.Secrets.Rules
{
    // A nuget.org API key. It lets anyone publish packages under Blacklight's name, so it is High
    // (Issue 3 design, Secret rules).
    public sealed class Sec006NuGetApiKeyRule : ISecretRule
    {
        public string RuleId => "SEC006";

        public Severity Severity => Severity.High;

        public string Message => "NuGet API key";

        // oy2 and exactly 43 lower-case letters and digits, as a whole token. Case-sensitive: the
        // format is lower case, so upper-case letters mean something else.
        public Regex Pattern { get; } = SecretRulePattern.Create(
            @"(?<![A-Za-z0-9])(?<secret>oy2[a-z0-9]{43})(?![A-Za-z0-9])");
    }
}
```

- [ ] **Step 28: Run it to make sure it passes**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~Sec006" -p:CollectCoverage=false`
Expected: `Passed: 5`.

- [ ] **Step 29: Write the failing SEC007 test**

`tests/RepoScanner.Scanner.Tests/Secrets/Rules/Sec007NamedSettingRuleTests.cs`:
```csharp
using NUnit.Framework;
using RepoScanner.Core.Enums;
using RepoScanner.Scanner.Secrets.Rules;

namespace RepoScanner.Scanner.Tests.Secrets.Rules
{
    [TestFixture]
    public class Sec007NamedSettingRuleTests
    {
        private readonly Sec007NamedSettingRule _rule = new Sec007NamedSettingRule();

        [Test]
        public void Rule_IsAMediumNamedSetting()
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_rule.RuleId, Is.EqualTo("SEC007"));
                Assert.That(_rule.Severity, Is.EqualTo(Severity.Medium));
                Assert.That(_rule.Message, Is.EqualTo("Password, secret or key in a named setting"));
            }
        }

        [TestCase("    \"ApiKey\": \"FAKE0payments0api0key0123456789\",", "FAKE0payments0api0key0123456789")]
        [TestCase("        private const string ApiKey = \"FAKE-csharp-key\";", "FAKE-csharp-key")]
        [TestCase("string password = @\"FAKE-verbatim\";", "FAKE-verbatim")]
        [TestCase("$apiKey = 'FAKE-powershell-key'", "FAKE-powershell-key")]
        [TestCase("  password: \"FAKE-yaml-quoted\"", "FAKE-yaml-quoted")]
        [TestCase("  password: FAKE-yaml-plain # a comment", "FAKE-yaml-plain")]
        [TestCase("    <add key=\"ClientSecret\" value=\"FAKE-client-secret-value-42\" />", "FAKE-client-secret-value-42")]
        [TestCase("DEVOPS_PAT=FAKEdevopspat000000000000000000000000000000000000000", "FAKEdevopspat000000000000000000000000000000000000000")]
        [TestCase("export DB_PASSWORD=FAKE-exported", "FAKE-exported")]
        [TestCase("\"AzureDevOpsPat\": \"FAKE-azure-devops-pat\"", "FAKE-azure-devops-pat")]
        [TestCase("pat: FAKE-plain-pat", "FAKE-plain-pat")]
        public void Pattern_FindsTheValue(string text, string expected)
        {
            Assert.That(RuleMatches.SecretsFoundBy(_rule, text), Is.EqualTo(new string[] { expected }));
        }

        // Names that only look secret, values that cannot be one, and ordinary lines of C#
        [TestCase("\"Path\": \"/var/FAKE-folder\"")]
        [TestCase("compat = \"FAKE-compat-value\"")]
        [TestCase("\"TokenEndpoint\": \"FAKE-token-endpoint\"")]
        [TestCase("\"SecretName\": \"FAKE-keyvault-secret-name\"")]
        [TestCase("\"Password\": \"short\"")]
        [TestCase("\"RefreshToken2\": \"https://login.example.com/token\"")]
        [TestCase("\"Password\": \"has spaces in it\"")]
        [TestCase("if (password == \"FAKE-compared-value\")")]
        [TestCase("            Password = options.Password;")]
        [TestCase("            ClientSecret = configuration[\"ClientSecret\"];")]
        [TestCase("    public Task RunAsync(CancellationToken cancellationToken)")]
        public void Pattern_IgnoresLookAlikes(string text)
        {
            Assert.That(RuleMatches.SecretsFoundBy(_rule, text), Is.Empty);
        }

        // The rule still matches a placeholder; the allow-list (#19) drops it in SecretScanner
        [Test]
        public void Pattern_StillMatchesAPlaceholder()
        {
            Assert.That(RuleMatches.SecretsFoundBy(_rule, "\"Password\": \"changeme\""), Is.EqualTo(new string[] { "changeme" }));
        }
    }
}
```

- [ ] **Step 30: Run it to make sure it fails**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~Sec007" -p:CollectCoverage=false`
Expected: build error `CS0246: The type or namespace name 'Sec007NamedSettingRule' could not be found`.

- [ ] **Step 31: Write SEC007**

`src/RepoScanner.Scanner/Secrets/Rules/Sec007NamedSettingRule.cs`:
```csharp
using System.Text.RegularExpressions;
using RepoScanner.Core.Enums;

namespace RepoScanner.Scanner.Secrets.Rules
{
    // A value held in a setting whose name suggests a secret: "Password", "ApiKey", "ClientSecret",
    // "DEVOPS_PAT" and so on. The catch-all after the specific rules, so it is Medium, and the rule the
    // placeholder allow-list matters most for (Issue 3 design, Secret rules).
    public sealed class Sec007NamedSettingRule : ISecretRule
    {
        // The name contains one of these words, or is or ends in "pat" (a personal access token). The
        // lookbehind makes the name start at a word boundary, so the "pat" in "compat" is never a name.
        // (?-i:Pat) is case-sensitive, so "AzureDevOpsPat" counts and "Path" does not.
        private const string SecretName =
            @"(?<![\w.$-])(?<name>" +
            @"[\w.$-]*(?:password|passwd|pwd|secret|token|apikey|api_key|api-key|accesskey|access_key)[\w.$-]*" +
            @"|pat" +
            @"|[\w.$-]*(?-i:Pat)" +
            @"|[\w.$-]*[_.-]pat" +
            @")" +
            // ...and does not end in a word naming something other than the secret, such as TokenEndpoint
            @"(?<!url|uri|endpoint|name|path|type|length|lifetime|timeout)";

        // At least 8 characters, no whitespace, and not a URL
        private const string QuotedValue = @"(?<secret>(?![^\s""']*://)[^\s""']{8,})";

        // The same for an unquoted value, which also stops at a comment (#) and at ; , ( and ), so a
        // line of code is not read as a value
        private const string UnquotedValue = @"(?<secret>(?![^\s""'#;,()]*://)[^\s""'#;,()]{8,})";

        // "Password": "...", ApiKey = "...", password: '...', $apiKey = @"..." (JSON, YAML, C#, PowerShell)
        private const string QuotedForm = SecretName + @"[""']?\s*[:=]\s*[@$]?[""']" + QuotedValue + @"[""']";

        // <add key="ClientSecret" value="..." /> (.config files)
        private const string XmlForm = @"\bkey\s*=\s*[""']" + SecretName + @"[""']\s+value\s*=\s*[""']" + QuotedValue + @"[""']";

        // NAME=value with no spaces around =, or name: value, at the start of a line (.env, shell and
        // YAML files). A C# assignment has spaces around =, so it is not read as this form.
        private const string UnquotedForm = @"^[ \t]*(?:export[ \t]+)?" + SecretName + @"(?:=|:[ \t]+)" + UnquotedValue;

        public string RuleId => "SEC007";

        public Severity Severity => Severity.Medium;

        public string Message => "Password, secret or key in a named setting";

        // Multiline: ^ in UnquotedForm is the start of any line, not only of the file
        public Regex Pattern { get; } = SecretRulePattern.Create(
            QuotedForm + "|" + XmlForm + "|" + UnquotedForm,
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
    }
}
```

- [ ] **Step 32: Run it to make sure it passes**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~Sec007" -p:CollectCoverage=false`
Expected: `Passed: 24`.

- [ ] **Step 33: Write the test across all rules**

`tests/RepoScanner.Scanner.Tests/Secrets/AllSecretRules.cs`:
```csharp
using RepoScanner.Scanner.Secrets;
using RepoScanner.Scanner.Secrets.Rules;

namespace RepoScanner.Scanner.Tests.Secrets
{
    // A new instance of each of the seven rules, in ID order, for tests that need all of them
    internal static class AllSecretRules
    {
        public static ISecretRule[] Create()
        {
            return new ISecretRule[]
            {
                new Sec001PrivateKeyRule(),
                new Sec002AzureSharedKeyRule(),
                new Sec003ConnectionStringPasswordRule(),
                new Sec004UrlCredentialsRule(),
                new Sec005EntraClientSecretRule(),
                new Sec006NuGetApiKeyRule(),
                new Sec007NamedSettingRule()
            };
        }
    }
}
```

`tests/RepoScanner.Scanner.Tests/Secrets/Rules/AllRulesTests.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using RepoScanner.Scanner.Secrets;

namespace RepoScanner.Scanner.Tests.Secrets.Rules
{
    [TestFixture]
    public class AllRulesTests
    {
        [Test]
        public void Rules_AreSec001ToSec007()
        {
            IEnumerable<string> ids = AllSecretRules.Create().Select(rule => rule.RuleId);

            Assert.That(ids, Is.EqualTo(new string[] { "SEC001", "SEC002", "SEC003", "SEC004", "SEC005", "SEC006", "SEC007" }));
        }

        // SecretScanner reads the "secret" group, and relies on the timeout to move past a slow pattern
        [TestCaseSource(typeof(AllSecretRules), nameof(AllSecretRules.Create))]
        public void Pattern_HasASecretGroupAndTheOneSecondTimeout(ISecretRule rule)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(rule.Pattern.GetGroupNames(), Does.Contain(SecretValue.GroupName), rule.RuleId);
                Assert.That(rule.Pattern.MatchTimeout, Is.EqualTo(TimeSpan.FromSeconds(1)), rule.RuleId);
                Assert.That(rule.Pattern.Options.HasFlag(RegexOptions.CultureInvariant), Is.True, rule.RuleId);
            }
        }
    }
}
```

- [ ] **Step 34: Run the rule tests**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~Secrets" -p:CollectCoverage=false`
Expected: `Passed: 80` (2 + 11 + 7 + 10 + 7 + 6 + 5 + 24 from the steps above, and 8 from `AllRulesTests`).

- [ ] **Step 35: Run everything**

Run: `dotnet test RepoScanner.sln`
Expected: every project `Passed!`, Scanner coverage at 80% or more.

- [ ] **Step 36: Stop for review, then commit**

Show the user the diff and wait for approval. Then:
```bash
git add src/RepoScanner.Scanner/Secrets tests/RepoScanner.Scanner.Tests/Secrets
git commit -m "This commit adds ISecretRule and the seven secret rules SEC001 to SEC007, each with its tests #17"
```

---

### Task 4: Placeholder allow-list (#19)

**Files:**
- Create: `src/RepoScanner.Scanner/Secrets/PlaceholderAllowList.cs`
- Test: `tests/RepoScanner.Scanner.Tests/Secrets/PlaceholderAllowListTests.cs`

**Interfaces:**
- Consumes: `SecretRulePattern.Create` (Task 3).
- Produces: `public static class PlaceholderAllowList { public static bool IsPlaceholder(string value); }` in `RepoScanner.Scanner.Secrets`.

- [ ] **Step 1: Write the failing test**

`tests/RepoScanner.Scanner.Tests/Secrets/PlaceholderAllowListTests.cs`:
```csharp
using NUnit.Framework;
using RepoScanner.Scanner.Secrets;

namespace RepoScanner.Scanner.Tests.Secrets
{
    [TestFixture]
    public class PlaceholderAllowListTests
    {
        [TestCase("changeme")]
        [TestCase("CHANGEME")]
        [TestCase("change-me")]
        [TestCase("password")]
        [TestCase("secret")]
        [TestCase("placeholder")]
        [TestCase("redacted")]
        [TestCase("todo")]
        [TestCase("none")]
        [TestCase("null")]
        [TestCase("example")]
        [TestCase("dummy")]
        [TestCase("  changeme  ")]
        public void IsPlaceholder_ForAPlaceholderWord_IsTrue(string value)
        {
            Assert.That(PlaceholderAllowList.IsPlaceholder(value), Is.True);
        }

        // A reference to a value kept somewhere else is not the value itself
        [TestCase("${DB_PASSWORD}")]
        [TestCase("$(Build.SecretValue)")]
        [TestCase("#{PaymentsWebhookSecret}#")]
        [TestCase("%API_KEY%")]
        [TestCase("{{ .Values.password }}")]
        [TestCase("__SmtpPassword__")]
        [TestCase("<your-password>")]
        public void IsPlaceholder_ForAReference_IsTrue(string value)
        {
            Assert.That(PlaceholderAllowList.IsPlaceholder(value), Is.True);
        }

        [TestCase("xxxxxxxx")]
        [TestCase("********")]
        [TestCase("00000000")]
        [TestCase("XXXXxxxx")]
        [TestCase("your-api-key-here")]
        [TestCase("YOUR_TOKEN")]
        public void IsPlaceholder_ForFiller_IsTrue(string value)
        {
            Assert.That(PlaceholderAllowList.IsPlaceholder(value), Is.True);
        }

        // Only whole values count, so a real secret cannot slip through by containing a placeholder word
        [TestCase("changeme123")]
        [TestCase("FAKEexampleKey1234")]
        [TestCase("Pa55word")]
        [TestCase("x")]
        [TestCase("yourpassword")]
        [TestCase("#{unclosed")]
        [TestCase("<>")]
        public void IsPlaceholder_ForAnythingElse_IsFalse(string value)
        {
            Assert.That(PlaceholderAllowList.IsPlaceholder(value), Is.False);
        }
    }
}
```

- [ ] **Step 2: Run it to make sure it fails**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~PlaceholderAllowList" -p:CollectCoverage=false`
Expected: build error `CS0103: The name 'PlaceholderAllowList' does not exist in the current context`.

- [ ] **Step 3: Write the allow-list**

`src/RepoScanner.Scanner/Secrets/PlaceholderAllowList.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RepoScanner.Scanner.Secrets
{
    // Values that look like secrets to a rule but are obviously not real ones: sample words, references
    // to a value kept somewhere else, and filler. Only whole values count, so "changeme123" is still
    // reported (Issue 3 design, Placeholder allow-list).
    public static class PlaceholderAllowList
    {
        private static readonly HashSet<string> Words = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "changeme", "change-me", "password", "secret", "placeholder", "redacted", "todo", "none", "null", "example", "dummy"
        };

        // ${NAME} and $(NAME) (shell, MSBuild and pipeline variables), #{NAME}# (Azure Pipelines token
        // replacement), %NAME% (Windows environment variables), {{NAME}} (templates), __NAME__ and <NAME>
        private static readonly Regex Reference = SecretRulePattern.Create(
            @"^(?:\$\{.+\}|\$\(.+\)|#\{.+\}#|%.+%|\{\{.+\}\}|__.+__|<.+>)$");

        public static bool IsPlaceholder(string value)
        {
            string trimmed = value.Trim();

            // An empty value has nothing to report
            return trimmed.Length == 0
                || Words.Contains(trimmed)
                || Reference.IsMatch(trimmed)
                || IsOneCharacterRepeated(trimmed)
                || trimmed.StartsWith("your-", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("your_", StringComparison.OrdinalIgnoreCase);
        }

        // xxxxxxxx, ********, 00000000: filler, not a secret. One character on its own is not filler.
        private static bool IsOneCharacterRepeated(string value)
        {
            char first = char.ToLowerInvariant(value[0]);
            return value.Length >= 2 && value.All(character => char.ToLowerInvariant(character) == first);
        }
    }
}
```

- [ ] **Step 4: Run it to make sure it passes**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~PlaceholderAllowList" -p:CollectCoverage=false`
Expected: `Passed: 33`.

- [ ] **Step 5: Run everything**

Run: `dotnet test RepoScanner.sln`
Expected: every project `Passed!`.

- [ ] **Step 6: Stop for review, then commit**

Show the user the diff and wait for approval. Then:
```bash
git add src/RepoScanner.Scanner/Secrets/PlaceholderAllowList.cs tests/RepoScanner.Scanner.Tests/Secrets/PlaceholderAllowListTests.cs
git commit -m "This commit adds the placeholder allow-list, so sample values and references to other settings are not reported #19"
```

---

### Task 5: Masking, hashing and ADR-023 (#18)

**Files:**
- Create: `src/RepoScanner.Scanner/Secrets/SecretMasker.cs`
- Create: `src/RepoScanner.Scanner/Secrets/SecretHasher.cs`
- Create: `docs/adr/ADR-023-secret-hash-is-an-unsalted-sha-256.md`
- Modify: `docs/adr/README.md` (index row, next ADR number)
- Test: `tests/RepoScanner.Scanner.Tests/Secrets/SecretMaskerTests.cs`
- Test: `tests/RepoScanner.Scanner.Tests/Secrets/SecretHasherTests.cs`

**Interfaces:**
- Produces (in `RepoScanner.Scanner.Secrets`):
  - `public static class SecretMasker { public const string Stars = "****"; public const int MaxCharactersShown = 4; public static string Mask(string secret); }`
  - `public static class SecretHasher { public static string Hash(string secret); }`

- [ ] **Step 1: Write the failing tests**

`tests/RepoScanner.Scanner.Tests/Secrets/SecretMaskerTests.cs`:
```csharp
using NUnit.Framework;
using RepoScanner.Scanner.Secrets;

namespace RepoScanner.Scanner.Tests.Secrets
{
    [TestFixture]
    public class SecretMaskerTests
    {
        // At most 4 characters, and never more than a quarter of the secret
        [TestCase("", "****")]
        [TestCase("abc", "****")]
        [TestCase("abcd", "a****")]
        [TestCase("Pa55word", "Pa****")]
        [TestCase("abcdefghijklmnop", "abcd****")]
        public void Mask_ShowsAtMostAQuarterOfTheSecret(string secret, string expected)
        {
            Assert.That(SecretMasker.Mask(secret), Is.EqualTo(expected));
        }

        // An 88-character storage key still shows only 4 characters
        [Test]
        public void Mask_ShowsNoMoreThanFourCharacters()
        {
            Assert.That(SecretMasker.Mask("FAKE" + new string('0', 84)), Is.EqualTo("FAKE****"));
        }

        // Always four stars, so the preview does not reveal how long the secret is
        [Test]
        public void Mask_DoesNotRevealTheLength()
        {
            Assert.That(SecretMasker.Mask(new string('a', 20)), Is.EqualTo(SecretMasker.Mask(new string('a', 80))));
        }
    }
}
```

`tests/RepoScanner.Scanner.Tests/Secrets/SecretHasherTests.cs`:
```csharp
using NUnit.Framework;
using RepoScanner.Scanner.Secrets;

namespace RepoScanner.Scanner.Tests.Secrets
{
    [TestFixture]
    public class SecretHasherTests
    {
        // The published SHA-256 test value for "abc" (FIPS 180-2)
        [Test]
        public void Hash_IsTheSha256OfTheUtf8Bytes()
        {
            Assert.That(SecretHasher.Hash("abc"), Is.EqualTo("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"));
        }

        // Fits the SecretHash column, varchar(64)
        [Test]
        public void Hash_Is64LowerCaseHexCharacters()
        {
            Assert.That(SecretHasher.Hash("FAKE-secret"), Does.Match("^[0-9a-f]{64}$"));
        }

        // ADR-023: unsalted, so the same secret always gives the same hash
        [Test]
        public void Hash_OfTheSameSecret_IsTheSame()
        {
            Assert.That(SecretHasher.Hash("FAKE-secret"), Is.EqualTo(SecretHasher.Hash("FAKE-secret")));
        }

        [Test]
        public void Hash_OfDifferentSecrets_IsDifferent()
        {
            Assert.That(SecretHasher.Hash("FAKE-secret-1"), Is.Not.EqualTo(SecretHasher.Hash("FAKE-secret-2")));
        }
    }
}
```

- [ ] **Step 2: Run them to make sure they fail**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~SecretMasker|FullyQualifiedName~SecretHasher" -p:CollectCoverage=false`
Expected: build errors `CS0103: The name 'SecretMasker' does not exist` and `The name 'SecretHasher' does not exist`.

- [ ] **Step 3: Write the masker and the hasher**

`src/RepoScanner.Scanner/Secrets/SecretMasker.cs`:
```csharp
using System;

namespace RepoScanner.Scanner.Secrets
{
    // The preview a person sees on the dashboard instead of the secret (Issue 3 design, Masking and
    // hashing). Enough to recognise which key it is, never enough to use it.
    public static class SecretMasker
    {
        // Always four, whatever the secret's length, so the preview does not reveal how long it is
        public const string Stars = "****";

        // Never more than 4 characters, and never more than a quarter of the secret
        public const int MaxCharactersShown = 4;

        public static string Mask(string secret)
        {
            int shown = Math.Min(MaxCharactersShown, secret.Length / 4);
            return secret.Substring(0, shown) + Stars;
        }
    }
}
```

`src/RepoScanner.Scanner/Secrets/SecretHasher.cs`:
```csharp
using System;
using System.Security.Cryptography;
using System.Text;

namespace RepoScanner.Scanner.Secrets
{
    // Lets a finding recognise the same secret across scans and files without storing it (ADR-023)
    public static class SecretHasher
    {
        // SHA-256 of the UTF-8 bytes as 64 lower-case hex characters, which fits the SecretHash column
        // (varchar(64)). Unsalted on purpose: the same secret must always give the same hash.
        public static string Hash(string secret)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
            return Convert.ToHexStringLower(hash);
        }
    }
}
```

- [ ] **Step 4: Run them to make sure they pass**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~SecretMasker|FullyQualifiedName~SecretHasher" -p:CollectCoverage=false`
Expected: `Passed: 11`.

- [ ] **Step 5: Write ADR-023**

`docs/adr/ADR-023-secret-hash-is-an-unsalted-sha-256.md`:
```markdown
# ADR-023: The secret hash is an unsalted SHA-256

- **Status:** Accepted
- **Date:** 2026-09-25
- **Source:** Decided while implementing Issue 3 (#14), task #18

## Context

A secret finding must be recognised as the same secret across scans and across files, without the secret itself being stored anywhere: the raw value is never stored (implementation brief ADR-011, Issue 2 design). The `Findings` table therefore holds a hash of it in `SecretHash`. The Issue 2 design recorded that an unsalted hash has a weakness, and gave this ADR to task #18.

## Decision

`SecretHash` is the SHA-256 of the secret's UTF-8 bytes, as 64 lower-case hex characters, with no salt and no key. `SecretHasher` in RepoScanner.Scanner computes it. Whitespace is removed from the secret first, so line endings do not change it.

## Consequences

Anyone who can read the `Findings` table could recover a short or common secret by hashing guesses and comparing the results. Long random values, such as storage account keys, private keys, NuGet API keys and Entra client secrets, cannot be recovered that way. Only the scanner, dashboard and pipeline identities can read the table (ADR-008, ADR-014), and the data stays in UK West (ADR-015).

A random salt per finding was rejected: the same secret would no longer give the same hash, which is the reason for storing one. A keyed hash (HMAC) with its key in Key Vault was rejected for now: it would protect short secrets, but it adds a second Key Vault secret, and ADR-007 allows exactly one. It is the upgrade to make if short passwords turn out to be common among the findings. Changing the hash changes the fingerprint of every secret finding, so the switch would go through the reconciler (Issue 4).
```

- [ ] **Step 6: Add ADR-023 to the index**

In `docs/adr/README.md`:
- Replace `The next new ADR is **ADR-023**.` with `The next new ADR is **ADR-024**.`
- After the ADR-022 row, add:
```markdown
| [ADR-023](ADR-023-secret-hash-is-an-unsalted-sha-256.md) | The secret hash is an unsalted SHA-256 | Accepted |
```

- [ ] **Step 7: Run everything**

Run: `dotnet test RepoScanner.sln`
Expected: every project `Passed!`.

- [ ] **Step 8: Stop for review, then commit**

Show the user the diff and wait for approval. Then:
```bash
git add src/RepoScanner.Scanner/Secrets/SecretMasker.cs src/RepoScanner.Scanner/Secrets/SecretHasher.cs tests/RepoScanner.Scanner.Tests/Secrets/SecretMaskerTests.cs tests/RepoScanner.Scanner.Tests/Secrets/SecretHasherTests.cs docs/adr/ADR-023-secret-hash-is-an-unsalted-sha-256.md docs/adr/README.md
git commit -m "This commit adds secret masking and SHA-256 hashing, and ADR-023 on why the hash is unsalted #18"
```

---

### Task 6: RepositoryFileWalker and the skip rules (#16)

**Files:**
- Create: `src/RepoScanner.Scanner/Files/RepositoryFile.cs`
- Create: `src/RepoScanner.Scanner/Files/RepositoryFileWalker.cs`
- Create: `tests/RepoScanner.Scanner.Tests/TempFolder.cs`
- Test: `tests/RepoScanner.Scanner.Tests/Files/RepositoryFileWalkerTests.cs`

**Interfaces:**
- Produces (in `RepoScanner.Scanner.Files`):
  - `public sealed record RepositoryFile(string FullPath, string RelativePath);`
  - `public sealed class RepositoryFileWalker { public const string ExclusionMarkerFileName = ".reposcanner-exclude"; public const long MaxFileSizeBytes = 1048576; public const int BinaryCheckByteCount = 8000; public RepositoryFileWalker(ILogger<RepositoryFileWalker> logger); public IReadOnlyList<RepositoryFile> GetFiles(string rootPath); }`
  - Summary log line with structured properties `FileCount`, `RootPath`, `FolderCount`, `ExcludedCount`, `LinkCount`, `LargeCount`, `BinaryCount`, `UnreadableCount`.
  - Test helper `internal sealed class TempFolder : IDisposable` in `RepoScanner.Scanner.Tests`: `string FullPath`, `string WriteText(string relativePath, string content)`, `string WriteBytes(string relativePath, byte[] content)` (both return the full path).

- [ ] **Step 1: Write the test helper**

`tests/RepoScanner.Scanner.Tests/TempFolder.cs`:
```csharp
using System;
using System.IO;

namespace RepoScanner.Scanner.Tests
{
    // A new, empty folder under the system temp folder for one test, deleted when the test ends
    internal sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            FullPath = Path.Combine(Path.GetTempPath(), "reposcanner-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(FullPath);
        }

        public string FullPath { get; }

        // Writes a file at a path relative to this folder (with / between folders), creating the
        // folders it needs, and returns its full path
        public string WriteText(string relativePath, string content)
        {
            string fullPath = PrepareFile(relativePath);
            File.WriteAllText(fullPath, content);
            return fullPath;
        }

        public string WriteBytes(string relativePath, byte[] content)
        {
            string fullPath = PrepareFile(relativePath);
            File.WriteAllBytes(fullPath, content);
            return fullPath;
        }

        public void Dispose()
        {
            // A symbolic link inside is deleted as a link; what it points to is left alone
            Directory.Delete(FullPath, recursive: true);
        }

        private string PrepareFile(string relativePath)
        {
            string fullPath = Path.Combine(FullPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            return fullPath;
        }
    }
}
```

- [ ] **Step 2: Write the failing tests**

`tests/RepoScanner.Scanner.Tests/Files/RepositoryFileWalkerTests.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using NUnit.Framework;
using RepoScanner.Scanner.Files;

namespace RepoScanner.Scanner.Tests.Files
{
    [TestFixture]
    public class RepositoryFileWalkerTests
    {
        private TempFolder _folder = null!;
        private FakeLogger<RepositoryFileWalker> _logger = null!;

        [SetUp]
        public void SetUp()
        {
            _folder = new TempFolder();
            _logger = new FakeLogger<RepositoryFileWalker>();
        }

        [TearDown]
        public void TearDown()
        {
            _folder.Dispose();
        }

        [Test]
        public void GetFiles_ListsFilesInEveryFolderWithForwardSlashesInOrder()
        {
            _folder.WriteText("b.txt", "x");
            _folder.WriteText("a/z.txt", "x");
            _folder.WriteText("a/b/c.json", "x");

            Assert.That(RelativePaths(), Is.EqualTo(new string[] { "a/b/c.json", "a/z.txt", "b.txt" }));
        }

        [Test]
        public void GetFiles_GivesTheFullPathOfEachFile()
        {
            string fullPath = _folder.WriteText("src/app.cs", "x");

            Assert.That(Walker().GetFiles(_folder.FullPath).Single().FullPath, Is.EqualTo(fullPath));
        }

        [Test]
        public void GetFiles_OnAnEmptyFolder_ListsNothing()
        {
            Assert.That(RelativePaths(), Is.Empty);
        }

        [TestCase(".git")]
        [TestCase("bin")]
        [TestCase("obj")]
        [TestCase("node_modules")]
        [TestCase("packages")]
        [TestCase(".vs")]
        [TestCase("TestResults")]
        [TestCase("BIN")]
        public void GetFiles_SkipsToolFoldersAtAnyDepth(string folderName)
        {
            _folder.WriteText(folderName + "/settings.json", "x");
            _folder.WriteText("src/" + folderName + "/deep.json", "x");
            _folder.WriteText("keep.txt", "x");

            Assert.That(RelativePaths(), Is.EqualTo(new string[] { "keep.txt" }));
        }

        // Exactly 1 MB is scanned; one byte more is not (#68)
        [Test]
        public void GetFiles_SkipsFilesLargerThanOneMegabyte()
        {
            _folder.WriteBytes("exact.txt", Filled(RepositoryFileWalker.MaxFileSizeBytes));
            _folder.WriteBytes("larger.txt", Filled(RepositoryFileWalker.MaxFileSizeBytes + 1));

            Assert.That(RelativePaths(), Is.EqualTo(new string[] { "exact.txt" }));
        }

        // A zero byte in the first 8,000 bytes means binary; one after them does not (#68)
        [Test]
        public void GetFiles_SkipsFilesWithAZeroByteInTheFirst8000Bytes()
        {
            byte[] early = Filled(100);
            early[50] = 0;
            byte[] late = Filled(9000);
            late[RepositoryFileWalker.BinaryCheckByteCount] = 0;
            _folder.WriteBytes("early.bin", early);
            _folder.WriteBytes("late.txt", late);

            Assert.That(RelativePaths(), Is.EqualTo(new string[] { "late.txt" }));
        }

        [Test]
        public void GetFiles_SkipsAFolderBelowTheRootThatHasTheMarker()
        {
            _folder.WriteText("vendor/" + RepositoryFileWalker.ExclusionMarkerFileName, string.Empty);
            _folder.WriteText("vendor/lib/settings.json", "x");
            _folder.WriteText("src/app.cs", "x");

            Assert.That(RelativePaths(), Is.EqualTo(new string[] { "src/app.cs" }));
        }

        // So --path on the fixture still scans it, although the fixture carries the marker
        [Test]
        public void GetFiles_IgnoresTheMarkerInTheRootItself()
        {
            _folder.WriteText(RepositoryFileWalker.ExclusionMarkerFileName, string.Empty);
            _folder.WriteText("src/app.cs", "x");

            Assert.That(RelativePaths(), Is.EqualTo(new string[] { RepositoryFileWalker.ExclusionMarkerFileName, "src/app.cs" }));
        }

        // A cloned repository could link to a file outside itself
        [Test]
        public void GetFiles_DoesNotFollowSymbolicLinks()
        {
            using (TempFolder outside = new TempFolder())
            {
                string outsideFile = outside.WriteText("outside.txt", "x");
                _folder.WriteText("real.txt", "x");
                try
                {
                    File.CreateSymbolicLink(Path.Combine(_folder.FullPath, "linked.txt"), outsideFile);
                    Directory.CreateSymbolicLink(Path.Combine(_folder.FullPath, "linked-folder"), outside.FullPath);
                }
                catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
                {
                    // Windows lets only an administrator, or a machine in Developer Mode, create links
                    Assert.Inconclusive("This machine does not let the test create symbolic links: " + exception.Message);
                }

                Assert.That(RelativePaths(), Is.EqualTo(new string[] { "real.txt" }));
            }
        }

        // Review focus: a file another program has locked must not stop the scan
        [Test]
        public void GetFiles_SkipsAFileItCannotOpenAndLogsAWarningNamingIt()
        {
            string lockedPath = _folder.WriteText("locked.log", "x");
            _folder.WriteText("open.txt", "x");

            IReadOnlyList<string> paths;
            using (FileStream locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                paths = RelativePaths();
            }

            FakeLogRecord warning = _logger.Collector.GetSnapshot().Single(record => record.Level == LogLevel.Warning);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(paths, Is.EqualTo(new string[] { "open.txt" }));
                Assert.That(warning.Message, Does.Contain("locked.log"));
            }
        }

        [Test]
        public void GetFiles_LogsHowManyItSkippedForEachReason()
        {
            byte[] binary = Filled(10);
            binary[0] = 0;
            _folder.WriteText("bin/tool.dll.config", "x");
            _folder.WriteText("vendor/" + RepositoryFileWalker.ExclusionMarkerFileName, string.Empty);
            _folder.WriteBytes("large.txt", Filled(RepositoryFileWalker.MaxFileSizeBytes + 1));
            _folder.WriteBytes("image.png", binary);
            _folder.WriteText("app.cs", "x");

            RelativePaths();

            FakeLogRecord summary = _logger.LatestRecord;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(summary.GetStructuredStateValue("FileCount"), Is.EqualTo("1"));
                Assert.That(summary.GetStructuredStateValue("FolderCount"), Is.EqualTo("1"));
                Assert.That(summary.GetStructuredStateValue("ExcludedCount"), Is.EqualTo("1"));
                Assert.That(summary.GetStructuredStateValue("LargeCount"), Is.EqualTo("1"));
                Assert.That(summary.GetStructuredStateValue("BinaryCount"), Is.EqualTo("1"));
                Assert.That(summary.GetStructuredStateValue("LinkCount"), Is.EqualTo("0"));
                Assert.That(summary.GetStructuredStateValue("UnreadableCount"), Is.EqualTo("0"));
            }
        }

        private IReadOnlyList<string> RelativePaths()
        {
            return Walker().GetFiles(_folder.FullPath).Select(file => file.RelativePath).ToList();
        }

        private RepositoryFileWalker Walker()
        {
            return new RepositoryFileWalker(_logger);
        }

        private static byte[] Filled(long length)
        {
            byte[] bytes = new byte[length];
            Array.Fill(bytes, (byte)'a');
            return bytes;
        }
    }
}
```

- [ ] **Step 3: Run them to make sure they fail**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~RepositoryFileWalker" -p:CollectCoverage=false`
Expected: build error `CS0234: The type or namespace name 'Files' does not exist in the namespace 'RepoScanner.Scanner'`.

- [ ] **Step 4: Write the walker**

`src/RepoScanner.Scanner/Files/RepositoryFile.cs`:
```csharp
namespace RepoScanner.Scanner.Files
{
    // One file to scan: where it is on disk, and its path within the scanned folder with / between
    // folders, which is what a finding records (Finding.FilePath)
    public sealed record RepositoryFile(string FullPath, string RelativePath);
}
```

`src/RepoScanner.Scanner/Files/RepositoryFileWalker.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;

namespace RepoScanner.Scanner.Files
{
    // Lists the files a scanner should read under a folder, leaving out what nobody wrote by hand:
    // tool folders, large and binary files, links, folders marked as excluded, and files that cannot be
    // opened (Issue 3 design, Skip rules). Shared by the secret, package and code-pattern scanners.
    public sealed class RepositoryFileWalker
    {
        // A folder holding this file is skipped, with everything below it, when it is below the scan
        // root. RepoScanner's own fake test secrets sit in such folders, so the nightly scan of this
        // repository does not report them.
        public const string ExclusionMarkerFileName = ".reposcanner-exclude";

        // Larger files are data or generated, not code or configuration (#68)
        public const long MaxFileSizeBytes = 1024 * 1024;

        // A zero byte in the first 8,000 bytes marks a file as binary: the test git uses (#68)
        public const int BinaryCheckByteCount = 8000;

        // Git internals, build output, downloaded packages, and IDE and test state
        private static readonly HashSet<string> SkippedFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", "bin", "obj", "node_modules", "packages", ".vs", "TestResults"
        };

        private readonly ILogger<RepositoryFileWalker> _logger;

        public RepositoryFileWalker(ILogger<RepositoryFileWalker> logger)
        {
            _logger = logger;
        }

        // Sorted by RelativePath (ordinal), so every scan of the same folder lists files in the same order
        public IReadOnlyList<RepositoryFile> GetFiles(string rootPath)
        {
            DirectoryInfo root = new DirectoryInfo(Path.GetFullPath(rootPath));
            List<RepositoryFile> files = new List<RepositoryFile>();
            SkipCounts skipped = new SkipCounts();

            AddFiles(root, root.FullName, isRoot: true, files, skipped);
            files.Sort((first, second) => string.CompareOrdinal(first.RelativePath, second.RelativePath));

            _logger.LogInformation(
                "Found {FileCount} files to scan under {RootPath}; skipped {FolderCount} tool folders, {ExcludedCount} excluded folders, " +
                "{LinkCount} links, {LargeCount} large files, {BinaryCount} binary files and {UnreadableCount} unreadable files",
                files.Count, root.FullName, skipped.ToolFolders, skipped.ExcludedFolders, skipped.Links,
                skipped.LargeFiles, skipped.BinaryFiles, skipped.UnreadableFiles);

            return files;
        }

        private void AddFiles(DirectoryInfo folder, string rootPath, bool isRoot, List<RepositoryFile> files, SkipCounts skipped)
        {
            // The marker in the scan root itself is ignored, so --path on the fixture still scans it
            if (!isRoot && File.Exists(Path.Combine(folder.FullName, ExclusionMarkerFileName)))
            {
                skipped.ExcludedFolders++;
                return;
            }

            foreach (FileInfo file in folder.EnumerateFiles())
            {
                string relativePath = Path.GetRelativePath(rootPath, file.FullName).Replace(Path.DirectorySeparatorChar, '/');

                if (file.LinkTarget != null)
                {
                    // A link could point outside the folder being scanned
                    skipped.Links++;
                }
                else if (file.Length > MaxFileSizeBytes)
                {
                    skipped.LargeFiles++;
                }
                else
                {
                    bool? binary = StartsLikeABinaryFile(file, relativePath);
                    if (binary == null)
                    {
                        skipped.UnreadableFiles++;
                    }
                    else if (binary.Value)
                    {
                        skipped.BinaryFiles++;
                    }
                    else
                    {
                        files.Add(new RepositoryFile(file.FullName, relativePath));
                    }
                }
            }

            foreach (DirectoryInfo child in folder.EnumerateDirectories())
            {
                if (child.LinkTarget != null)
                {
                    skipped.Links++;
                }
                else if (SkippedFolderNames.Contains(child.Name))
                {
                    skipped.ToolFolders++;
                }
                else
                {
                    AddFiles(child, rootPath, isRoot: false, files, skipped);
                }
            }
        }

        // True for a binary file, false for text, and null when the file cannot be opened
        private bool? StartsLikeABinaryFile(FileInfo file, string relativePath)
        {
            try
            {
                using (FileStream stream = file.OpenRead())
                {
                    byte[] start = new byte[BinaryCheckByteCount];
                    int read = stream.ReadAtLeast(start, BinaryCheckByteCount, throwOnEndOfStream: false);
                    return Array.IndexOf(start, (byte)0, 0, read) >= 0;
                }
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                // For example another program has it locked. The rest of the folder is still scanned.
                _logger.LogWarning("Skipped {FilePath}: it could not be opened ({Reason})", relativePath, exception.Message);
                return null;
            }
        }

        // How many of each kind of thing were skipped, for the one summary log line
        private sealed class SkipCounts
        {
            public int ToolFolders { get; set; }

            public int ExcludedFolders { get; set; }

            public int Links { get; set; }

            public int LargeFiles { get; set; }

            public int BinaryFiles { get; set; }

            public int UnreadableFiles { get; set; }
        }
    }
}
```

- [ ] **Step 5: Run them to make sure they pass**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~RepositoryFileWalker" -p:CollectCoverage=false`
Expected: 18 tests pass (the symbolic link test may instead show as skipped, because it is inconclusive on a Windows machine without Developer Mode).

- [ ] **Step 6: Run everything**

Run: `dotnet test RepoScanner.sln`
Expected: every project `Passed!`.

- [ ] **Step 7: Stop for review, then commit**

Show the user the diff and wait for approval. Then:
```bash
git add src/RepoScanner.Scanner/Files tests/RepoScanner.Scanner.Tests/Files tests/RepoScanner.Scanner.Tests/TempFolder.cs
git commit -m "This commit adds RepositoryFileWalker, which skips tool folders, large, binary, linked, excluded and unreadable files #16"
```

---

### Task 7: SecretScanner (#16)

**Files:**
- Create: `src/RepoScanner.Scanner/Secrets/SecretScanner.cs`
- Test: `tests/RepoScanner.Scanner.Tests/Secrets/SecretScannerTests.cs`

**Interfaces:**
- Consumes: `IScanner`, `RawFinding` (Task 2); `ISecretRule`, `SecretValue` (Task 3); `PlaceholderAllowList` (Task 4); `SecretMasker`, `SecretHasher` (Task 5); `RepositoryFileWalker`, `RepositoryFile` (Task 6); test helpers `TempFolder`, `AllSecretRules`.
- Produces: `public sealed class SecretScanner : IScanner` in `RepoScanner.Scanner.Secrets`, constructor `SecretScanner(RepositoryFileWalker walker, IEnumerable<ISecretRule> rules, ILogger<SecretScanner> logger)`. Summary log line with structured properties `FindingCount`, `FileCount`, `PlaceholderCount`.

- [ ] **Step 1: Write the failing tests**

`tests/RepoScanner.Scanner.Tests/Secrets/SecretScannerTests.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using NUnit.Framework;
using RepoScanner.Core.Enums;
using RepoScanner.Core.Scanning;
using RepoScanner.Scanner.Files;
using RepoScanner.Scanner.Secrets;
using RepoScanner.Scanner.Secrets.Rules;

namespace RepoScanner.Scanner.Tests.Secrets
{
    [TestFixture]
    public class SecretScannerTests
    {
        private const string NuGetKey = "oy2fakenugetapikeyforreposcannertests000000000";
        private const string ClientSecret = "FAK8Q~FAKE.client_secret~for-RepoScanner";

        private TempFolder _folder = null!;
        private FakeLogger<SecretScanner> _logger = null!;

        [SetUp]
        public void SetUp()
        {
            _folder = new TempFolder();
            _logger = new FakeLogger<SecretScanner>();
        }

        [TearDown]
        public void TearDown()
        {
            _folder.Dispose();
        }

        [Test]
        public void ScannerType_IsSecret()
        {
            Assert.That(CreateScanner().ScannerType, Is.EqualTo(ScannerType.Secret));
        }

        [Test]
        public async Task ScanAsync_ReportsTheRuleAndWhereTheSecretIs()
        {
            _folder.WriteText("deploy/publish.ps1", "# Publishes the package\n\ndotnet nuget push out.nupkg --api-key " + NuGetKey + "\n");

            RawFinding finding = (await ScanAsync()).Single();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(finding.ScannerType, Is.EqualTo(ScannerType.Secret));
                Assert.That(finding.RuleId, Is.EqualTo("SEC006"));
                Assert.That(finding.Severity, Is.EqualTo(Severity.High));
                Assert.That(finding.Message, Is.EqualTo("NuGet API key"));
                Assert.That(finding.FilePath, Is.EqualTo("deploy/publish.ps1"));
                Assert.That(finding.LineNumber, Is.EqualTo(3));
            }
        }

        [Test]
        public async Task ScanAsync_MasksAndHashesTheSecret()
        {
            _folder.WriteText("publish.ps1", "--api-key " + NuGetKey);

            RawFinding finding = (await ScanAsync()).Single();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(finding.MaskedPreview, Is.EqualTo("oy2f****"));
                Assert.That(finding.SecretHash, Is.EqualTo(SecretHasher.Hash(NuGetKey)));
            }
        }

        // Review focus: git on Windows usually checks files out with CRLF line endings, and that
        // must not change a finding's line or its hash
        [Test]
        public async Task ScanAsync_WithWindowsLineEndings_ReportsTheSameLinesAndHashes()
        {
            string content = "# settings\n"
                + "-----BEGIN RSA PRIVATE KEY-----\n"
                + "MIIEFAKEprivateKeyForRepoScannerTestsAAAAAAAAAAAAAAAAAAAAAAAAAAA\n"
                + "FAKEsecondLineOfTheKeyBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB\n"
                + "-----END RSA PRIVATE KEY-----\n"
                + "--api-key " + NuGetKey + "\n";
            _folder.WriteText("lf/key.txt", content);
            _folder.WriteText("crlf/key.txt", content.Replace("\n", "\r\n"));

            IReadOnlyList<RawFinding> findings = await ScanAsync();
            List<(string RuleId, int? LineNumber, string? SecretHash)> lf = FindingsIn(findings, "lf/");
            List<(string RuleId, int? LineNumber, string? SecretHash)> crlf = FindingsIn(findings, "crlf/");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(lf.Select(finding => finding.LineNumber), Is.EqualTo(new int?[] { 2, 6 }));
                Assert.That(crlf, Is.EqualTo(lf));
            }
        }

        // Both SEC005 and SEC007 match a client secret held in a setting named ...Secret. It is
        // reported once, by the lower-numbered, more specific rule.
        [Test]
        public async Task ScanAsync_ReportsAnOverlapOnceUnderTheLowerNumberedRule()
        {
            _folder.WriteText("Settings.cs", "public const string GraphClientSecret = \"" + ClientSecret + "\";\n");

            IReadOnlyList<RawFinding> findings = await ScanAsync();

            Assert.That(findings.Select(finding => finding.RuleId), Is.EqualTo(new string[] { "SEC005" }));
        }

        // The rules can be given in any order; they still run in ID order, so the specific rule wins
        [Test]
        public async Task ScanAsync_RunsTheRulesInIdOrderWhateverOrderTheyAreGivenIn()
        {
            _folder.WriteText("Settings.cs", "public const string GraphClientSecret = \"" + ClientSecret + "\";\n");
            SecretScanner scanner = new SecretScanner(Walker(), new ISecretRule[] { new Sec007NamedSettingRule(), new Sec005EntraClientSecretRule() }, _logger);

            IReadOnlyList<RawFinding> findings = await scanner.ScanAsync(_folder.FullPath, CancellationToken.None);

            Assert.That(findings.Select(finding => finding.RuleId), Is.EqualTo(new string[] { "SEC005" }));
        }

        [Test]
        public async Task ScanAsync_IgnoresPlaceholdersAndLogsHowMany()
        {
            _folder.WriteText("appsettings.json", "{\n  \"Password\": \"changeme\",\n  \"ApiKey\": \"#{ApiKey}#\"\n}\n");

            IReadOnlyList<RawFinding> findings = await ScanAsync();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(findings, Is.Empty);
                Assert.That(_logger.LatestRecord.GetStructuredStateValue("PlaceholderCount"), Is.EqualTo("2"));
            }
        }

        [Test]
        public async Task ScanAsync_ReturnsFindingsSortedByFileThenLine()
        {
            _folder.WriteText("b.ps1", "--api-key " + NuGetKey + "\n");
            _folder.WriteText("a.ps1", "\n\n--api-key " + NuGetKey + "\n# " + ClientSecret + "\n");

            IReadOnlyList<RawFinding> findings = await ScanAsync();

            Assert.That(findings.Select(finding => (finding.FilePath, finding.LineNumber)),
                Is.EqualTo(new (string, int?)[] { ("a.ps1", 3), ("a.ps1", 4), ("b.ps1", 1) }));
        }

        // A pattern that times out on one file is skipped for that file only
        [Test]
        public async Task ScanAsync_WhenARuleTimesOut_LogsAWarningAndStillRunsTheOtherRules()
        {
            _folder.WriteText("slow.txt", new string('a', 40) + "!\n--api-key " + NuGetKey + "\n");
            SecretScanner scanner = new SecretScanner(Walker(), new ISecretRule[] { new SlowRule(), new Sec006NuGetApiKeyRule() }, _logger);

            IReadOnlyList<RawFinding> findings = await scanner.ScanAsync(_folder.FullPath, CancellationToken.None);

            FakeLogRecord warning = _logger.Collector.GetSnapshot().Single(record => record.Level == LogLevel.Warning);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(findings.Select(finding => finding.RuleId), Is.EqualTo(new string[] { "SEC006" }));
                Assert.That(warning.Message, Does.Contain("SEC000").And.Contain("slow.txt"));
                Assert.That(warning.Exception, Is.Null, "The timeout's Input property holds the file's text");
            }
        }

        // Review focus: an old file saved as Windows-1252 is not valid UTF-8, but is still scanned
        [Test]
        public async Task ScanAsync_ScansAFileThatIsNotValidUtf8()
        {
            // In Latin-1 and Windows-1252, "£" is the byte 0xA3, which is not valid UTF-8 on its own
            _folder.WriteBytes("legacy.ps1", Encoding.Latin1.GetBytes("# Price: £5\n--api-key " + NuGetKey + "\n"));

            RawFinding finding = (await ScanAsync()).Single();

            Assert.That(finding.LineNumber, Is.EqualTo(2));
        }

        [Test]
        public async Task ScanAsync_WhenCancelled_Throws()
        {
            _folder.WriteText("a.txt", "x");
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();

                await Assert.ThatAsync(() => CreateScanner().ScanAsync(_folder.FullPath, cancellation.Token),
                    Throws.InstanceOf<OperationCanceledException>());
            }
        }

        private SecretScanner CreateScanner()
        {
            return new SecretScanner(Walker(), AllSecretRules.Create(), _logger);
        }

        private static RepositoryFileWalker Walker()
        {
            return new RepositoryFileWalker(NullLogger<RepositoryFileWalker>.Instance);
        }

        private Task<IReadOnlyList<RawFinding>> ScanAsync()
        {
            return CreateScanner().ScanAsync(_folder.FullPath, CancellationToken.None);
        }

        private static List<(string RuleId, int? LineNumber, string? SecretHash)> FindingsIn(IReadOnlyList<RawFinding> findings, string folder)
        {
            return findings
                .Where(finding => finding.FilePath.StartsWith(folder, StringComparison.Ordinal))
                .Select(finding => (finding.RuleId, finding.LineNumber, finding.SecretHash))
                .ToList();
        }

        // A rule whose pattern backtracks exponentially on a run of a's followed by something else.
        // Its own 10 ms timeout keeps the test fast; the real rules use one second.
        private sealed class SlowRule : ISecretRule
        {
            public string RuleId => "SEC000";

            public Severity Severity => Severity.Low;

            public string Message => "Slow rule for the timeout test";

            public Regex Pattern { get; } = new Regex(@"^(?<secret>(a|aa)+)$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(10));
        }
    }
}
```

- [ ] **Step 2: Run them to make sure they fail**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~SecretScannerTests" -p:CollectCoverage=false`
Expected: build error `CS0246: The type or namespace name 'SecretScanner' could not be found`.

- [ ] **Step 3: Write the scanner**

`src/RepoScanner.Scanner/Secrets/SecretScanner.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RepoScanner.Core.Enums;
using RepoScanner.Core.Scanning;
using RepoScanner.Scanner.Files;

namespace RepoScanner.Scanner.Secrets
{
    // Finds hard-coded secrets (SEC001 to SEC007) in the files under a folder. A secret's raw value
    // exists only inside AddFindings: it goes to the allow-list, the masker and the hasher and nowhere
    // else, so it never reaches a finding, a log line or the database (Issue 3 design).
    public sealed class SecretScanner : IScanner
    {
        private readonly RepositoryFileWalker _walker;
        private readonly IReadOnlyList<ISecretRule> _rules;
        private readonly ILogger<SecretScanner> _logger;

        public SecretScanner(RepositoryFileWalker walker, IEnumerable<ISecretRule> rules, ILogger<SecretScanner> logger)
        {
            _walker = walker;

            // Sorted by ID rather than trusting the registration order: the lower-numbered, more specific
            // rule must run first to win an overlap
            _rules = rules.OrderBy(rule => rule.RuleId, StringComparer.Ordinal).ToList();
            _logger = logger;
        }

        public ScannerType ScannerType => ScannerType.Secret;

        public async Task<IReadOnlyList<RawFinding>> ScanAsync(string rootPath, CancellationToken cancellationToken)
        {
            IReadOnlyList<RepositoryFile> files = _walker.GetFiles(rootPath);
            List<RawFinding> findings = new List<RawFinding>();
            int placeholderCount = 0;

            foreach (RepositoryFile file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // UTF-8, honouring a byte order mark. Bytes that are not valid UTF-8 become U+FFFD rather
                // than failing, so the rest of the file is still scanned.
                string text = await File.ReadAllTextAsync(file.FullPath, Encoding.UTF8, cancellationToken);
                placeholderCount += AddFindings(file.RelativePath, text, findings);
            }

            List<RawFinding> sorted = findings
                .OrderBy(finding => finding.FilePath, StringComparer.Ordinal)
                .ThenBy(finding => finding.LineNumber)
                .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
                .ToList();

            _logger.LogInformation("Secret scan found {FindingCount} findings in {FileCount} files and ignored {PlaceholderCount} placeholders",
                sorted.Count, files.Count, placeholderCount);

            return sorted;
        }

        // Runs every rule over one file's text and adds what they find. Returns how many placeholders
        // were ignored.
        private int AddFindings(string filePath, string text, List<RawFinding> findings)
        {
            // Where the secrets already reported in this file are, so a later rule cannot report one again
            List<Group> kept = new List<Group>();
            int placeholderCount = 0;

            foreach (ISecretRule rule in _rules)
            {
                foreach (Match match in MatchesOf(rule, filePath, text))
                {
                    Group secretGroup = match.Groups[SecretValue.GroupName];
                    string secret = SecretValue.FromMatch(match);

                    if (PlaceholderAllowList.IsPlaceholder(secret))
                    {
                        placeholderCount++;
                        continue;
                    }

                    if (kept.Any(other => Overlaps(other, secretGroup)))
                    {
                        continue;
                    }

                    kept.Add(secretGroup);
                    findings.Add(new RawFinding
                    {
                        ScannerType = ScannerType.Secret,
                        RuleId = rule.RuleId,
                        Severity = rule.Severity,
                        FilePath = filePath,
                        LineNumber = LineNumberAt(text, match.Index),
                        Message = rule.Message,
                        MaskedPreview = SecretMasker.Mask(secret),
                        SecretHash = SecretHasher.Hash(secret)
                    });
                }
            }

            return placeholderCount;
        }

        // Every match of one rule in the text. A pattern that times out on this file is skipped for this
        // file only. The exception is not logged, because its Input property holds the file's text.
        private IReadOnlyList<Match> MatchesOf(ISecretRule rule, string filePath, string text)
        {
            try
            {
                // Matches are found lazily, so all of them are found here, inside the try
                return rule.Pattern.Matches(text).ToList();
            }
            catch (RegexMatchTimeoutException)
            {
                _logger.LogWarning("Rule {RuleId} timed out on {FilePath}; the other rules still ran on it", rule.RuleId, filePath);
                return Array.Empty<Match>();
            }
        }

        private static bool Overlaps(Group first, Group second)
        {
            return first.Index < second.Index + second.Length && second.Index < first.Index + first.Length;
        }

        // Counted from 1: one more than the number of line feeds before the match. Windows line endings
        // (\r\n) end in \n too, so they give the same number.
        private static int LineNumberAt(string text, int index)
        {
            return text.AsSpan(0, index).Count('\n') + 1;
        }
    }
}
```

- [ ] **Step 4: Run them to make sure they pass**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~SecretScannerTests" -p:CollectCoverage=false`
Expected: `Passed: 11`.

- [ ] **Step 5: Run everything**

Run: `dotnet test RepoScanner.sln`
Expected: every project `Passed!`, Scanner coverage at 80% or more.

- [ ] **Step 6: Stop for review, then commit**

Show the user the diff and wait for approval. Then:
```bash
git add src/RepoScanner.Scanner/Secrets/SecretScanner.cs tests/RepoScanner.Scanner.Tests/Secrets/SecretScannerTests.cs
git commit -m "This commit adds SecretScanner, which runs the rules over each file and masks and hashes what they find #16"
```

---

### Task 8: The secrets-repo fixture, markers and leak tests (#21, #18)

**Files:**
- Create: the fixture, `tests/fixtures/secrets-repo/` (11 files, below)
- Create: `tests/RepoScanner.Scanner.Tests/Secrets/.reposcanner-exclude`
- Create: `tests/RepoScanner.Scanner.Tests/RepositoryRoot.cs`
- Create: `tests/RepoScanner.Scanner.Tests/Secrets/FixtureSecret.cs`
- Create: `tests/RepoScanner.Scanner.Tests/Secrets/FixtureSecrets.cs`
- Test: `tests/RepoScanner.Scanner.Tests/Secrets/FixtureScanTests.cs`

**Interfaces:**
- Consumes: `SecretScanner` (Task 7), `RepositoryFileWalker` (Task 6), `SecretMasker`, `SecretHasher` (Task 5), `AllSecretRules`.
- Produces (test helpers):
  - `internal static class RepositoryRoot { public static string FullPath { get; } }` in `RepoScanner.Scanner.Tests`
  - `internal sealed record FixtureSecret(string RuleId, string FilePath, int LineNumber, string Value);` in `RepoScanner.Scanner.Tests.Secrets`
  - `internal static class FixtureSecrets { public static string FolderPath { get; } public static IReadOnlyList<FixtureSecret> All { get; } }` in `RepoScanner.Scanner.Tests.Secrets`

- [ ] **Step 1: Create the fixture files**

Create each file with exactly this content, each ending with a newline. Line numbers matter: `FixtureSecrets` refers to them.

`tests/fixtures/secrets-repo/.reposcanner-exclude`:
```text
# RepoScanner skips any folder that contains this file when it scans a folder above it.
# Everything in this folder is fake test data.
```

`tests/fixtures/secrets-repo/README.md`:
````markdown
# secrets-repo fixture

A small, pretend repository for RepoScanner's tests and demo. Every secret in it is fake and has never worked anywhere.

Scan it with:

    dotnet run --project src/RepoScanner.Scanner -- --path tests/fixtures/secrets-repo

The `.reposcanner-exclude` file stops these fake secrets being reported when the whole RepoScanner repository is scanned.
The expected findings are listed in `tests/RepoScanner.Scanner.Tests/Secrets/FixtureSecrets.cs`.
````

`tests/fixtures/secrets-repo/src/Api/appsettings.json` (SEC003 on line 3, SEC007 on line 6, placeholders on lines 7 and 8, an excluded name on line 11):
```json
{
  "ConnectionStrings": {
    "Orders": "Server=tcp:orders-FAKE.database.windows.net,1433;Database=Orders;User ID=orders_app;Password=FAKE-Orders-Pa55word;Encrypt=True"
  },
  "Payments": {
    "ApiKey": "FAKE0payments0api0key0123456789",
    "Password": "changeme",
    "WebhookSecret": "#{PaymentsWebhookSecret}#"
  },
  "Auth": {
    "TokenEndpoint": "https://login.example.com/oauth2/token"
  }
}
```

`tests/fixtures/secrets-repo/src/Api/Web.config` (SEC007 on line 5, a placeholder on line 6):
```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <appSettings>
    <add key="ClientId" value="00000000-0000-0000-0000-000000000000" />
    <add key="ClientSecret" value="FAKE-client-secret-value-42" />
    <add key="SmtpPassword" value="__SmtpPassword__" />
  </appSettings>
</configuration>
```

`tests/fixtures/secrets-repo/src/Api/StorageSettings.cs` (SEC002 on line 7; SEC005 on line 9, where SEC007 also matches and loses the overlap):
```csharp
namespace Fixture.Api
{
    // Fake values for RepoScanner's tests: none of them has ever worked anywhere
    public static class StorageSettings
    {
        public const string Connection =
            "DefaultEndpointsProtocol=https;AccountName=fakeaccount;AccountKey=FAKEstorageAccountKeyForRepoScannerTests0000000000000000000000000000000000000000000000==;EndpointSuffix=core.windows.net";

        public const string GraphClientSecret = "FAK8Q~FAKE.client_secret~for-RepoScanner";
    }
}
```

`tests/fixtures/secrets-repo/deploy/signing-key.pem` (SEC001 on line 1):
```text
-----BEGIN RSA PRIVATE KEY-----
MIIEFAKEprivateKeyForRepoScannerTestsAAAAAAAAAAAAAAAAAAAAAAAAAAA
FAKEsecondLineOfTheKeyBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB
-----END RSA PRIVATE KEY-----
```

`tests/fixtures/secrets-repo/deploy/publish.ps1` (SEC004 on line 2, SEC006 on line 3):
```powershell
# Publishes the package to the internal feed. Fake values for RepoScanner's tests.
$feed = "https://build-agent:FAKE-feed-pa55@pkgs.example.com/nuget/v3/index.json"
dotnet nuget push ./out/*.nupkg --source $feed --api-key oy2fakenugetapikeyforreposcannertests000000000
```

`tests/fixtures/secrets-repo/deploy/app.env` (SEC007 on line 2; named `app.env` because `.gitignore` excludes `.env`):
```text
# Settings for the container. Fake values for RepoScanner's tests.
DEVOPS_PAT=FAKEdevopspat000000000000000000000000000000000000000
LOG_LEVEL=Information
```

`tests/fixtures/secrets-repo/node_modules/tool/config.json` (in a skipped folder, so not reported; `node_modules` rather than `bin`, which `.gitignore` excludes):
```json
{
  "apiKey": "FAKE-this-is-in-a-skipped-folder"
}
```

`tests/fixtures/secrets-repo/vendor/.reposcanner-exclude`:
```text
# A marker below the scanned folder: this folder is skipped.
```

`tests/fixtures/secrets-repo/vendor/settings.json` (below a marker, so not reported):
```json
{
  "password": "FAKE-this-is-below-a-marker"
}
```

- [ ] **Step 2: Mark the test folder that holds fake secrets**

`tests/RepoScanner.Scanner.Tests/Secrets/.reposcanner-exclude`:
```text
# RepoScanner skips any folder that contains this file when it scans a folder above it.
# The tests in this folder contain fake secrets.
```

- [ ] **Step 3: Check git will commit every fixture file**

Run: `git status --short --untracked-files=all tests/fixtures tests/RepoScanner.Scanner.Tests/Secrets/.reposcanner-exclude`
Expected: 12 lines starting `??`, including `node_modules/tool/config.json`, `deploy/app.env` and both markers. If any is missing, `.gitignore` is excluding it: rename the file rather than changing `.gitignore`.

- [ ] **Step 4: Write the fixture's labels and the repository root helper**

`tests/RepoScanner.Scanner.Tests/RepositoryRoot.cs`:
```csharp
using System;
using System.IO;

namespace RepoScanner.Scanner.Tests
{
    // The folder holding RepoScanner.sln, found by walking up from the test's output folder, so a test
    // can read files in the repository wherever it is checked out
    internal static class RepositoryRoot
    {
        public static string FullPath { get; } = Find();

        private static string Find()
        {
            DirectoryInfo? folder = new DirectoryInfo(AppContext.BaseDirectory);
            while (folder != null && !File.Exists(Path.Combine(folder.FullName, "RepoScanner.sln")))
            {
                folder = folder.Parent;
            }

            if (folder == null)
            {
                throw new InvalidOperationException("RepoScanner.sln was not found above " + AppContext.BaseDirectory);
            }

            return folder.FullName;
        }
    }
}
```

`tests/RepoScanner.Scanner.Tests/Secrets/FixtureSecret.cs`:
```csharp
namespace RepoScanner.Scanner.Tests.Secrets
{
    // One fake secret in the fixture: the rule that must report it, where, and its value with
    // whitespace removed, as SecretScanner reads it
    internal sealed record FixtureSecret(string RuleId, string FilePath, int LineNumber, string Value);
}
```

`tests/RepoScanner.Scanner.Tests/Secrets/FixtureSecrets.cs`:
```csharp
using System.Collections.Generic;
using System.IO;

namespace RepoScanner.Scanner.Tests.Secrets
{
    // The labels for tests/fixtures/secrets-repo (#21): every fake secret in it and where the scanner
    // must report it. This folder has its own .reposcanner-exclude, so the nightly scan of this
    // repository does not report these values either.
    internal static class FixtureSecrets
    {
        public static string FolderPath { get; } = Path.Combine(RepositoryRoot.FullPath, "tests", "fixtures", "secrets-repo");

        // In the order the scanner returns them: by file (ordinal, so upper case before lower), then line
        public static IReadOnlyList<FixtureSecret> All { get; } = new FixtureSecret[]
        {
            new FixtureSecret("SEC007", "deploy/app.env", 2, "FAKEdevopspat000000000000000000000000000000000000000"),
            new FixtureSecret("SEC004", "deploy/publish.ps1", 2, "FAKE-feed-pa55"),
            new FixtureSecret("SEC006", "deploy/publish.ps1", 3, "oy2fakenugetapikeyforreposcannertests000000000"),
            new FixtureSecret("SEC001", "deploy/signing-key.pem", 1,
                "MIIEFAKEprivateKeyForRepoScannerTestsAAAAAAAAAAAAAAAAAAAAAAAAAAA" + "FAKEsecondLineOfTheKeyBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB"),
            new FixtureSecret("SEC002", "src/Api/StorageSettings.cs", 7, "FAKEstorageAccountKeyForRepoScannerTests0000000000000000000000000000000000000000000000=="),
            new FixtureSecret("SEC005", "src/Api/StorageSettings.cs", 9, "FAK8Q~FAKE.client_secret~for-RepoScanner"),
            new FixtureSecret("SEC007", "src/Api/Web.config", 5, "FAKE-client-secret-value-42"),
            new FixtureSecret("SEC003", "src/Api/appsettings.json", 3, "FAKE-Orders-Pa55word"),
            new FixtureSecret("SEC007", "src/Api/appsettings.json", 6, "FAKE0payments0api0key0123456789")
        };
    }
}
```

- [ ] **Step 5: Write the fixture and leak tests**

`tests/RepoScanner.Scanner.Tests/Secrets/FixtureScanTests.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Testing;
using NUnit.Framework;
using RepoScanner.Core.Scanning;
using RepoScanner.Scanner.Files;
using RepoScanner.Scanner.Secrets;

namespace RepoScanner.Scanner.Tests.Secrets
{
    [TestFixture]
    public class FixtureScanTests
    {
        private FakeLogger<SecretScanner> _scannerLogger = null!;
        private FakeLogger<RepositoryFileWalker> _walkerLogger = null!;
        private IReadOnlyList<RawFinding> _findings = null!;

        [SetUp]
        public async Task SetUp()
        {
            _scannerLogger = new FakeLogger<SecretScanner>();
            _walkerLogger = new FakeLogger<RepositoryFileWalker>();
            SecretScanner scanner = new SecretScanner(new RepositoryFileWalker(_walkerLogger), AllSecretRules.Create(), _scannerLogger);

            _findings = await scanner.ScanAsync(FixtureSecrets.FolderPath, CancellationToken.None);
        }

        // Issue 3 criterion 1, at the scanner: exactly the labelled secrets, each where its label says
        [Test]
        public void ScanAsync_OnTheFixture_FindsExactlyTheLabelledSecrets()
        {
            IEnumerable<(string, string, int?)> found = _findings.Select(finding => (finding.RuleId, finding.FilePath, finding.LineNumber));
            IEnumerable<(string, string, int?)> labelled = FixtureSecrets.All.Select(secret => (secret.RuleId, secret.FilePath, (int?)secret.LineNumber));

            Assert.That(found, Is.EqualTo(labelled));
        }

        [Test]
        public void ScanAsync_OnTheFixture_MasksAndHashesEachSecret()
        {
            Assert.That(_findings, Has.Count.EqualTo(FixtureSecrets.All.Count));

            for (int index = 0; index < _findings.Count; index++)
            {
                FixtureSecret secret = FixtureSecrets.All[index];
                string label = secret.RuleId + " in " + secret.FilePath;

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(_findings[index].MaskedPreview, Is.EqualTo(SecretMasker.Mask(secret.Value)), label);
                    Assert.That(_findings[index].SecretHash, Is.EqualTo(SecretHasher.Hash(secret.Value)), label);
                }
            }
        }

        // #18 and Issue 3 criterion 2: no raw value in a finding. A record's ToString lists every property.
        [Test]
        public void ScanAsync_OnTheFixture_PutsNoRawValueInAFinding()
        {
            string findingsText = string.Join("\n", _findings.Select(finding => finding.ToString()));

            AssertNoValueLeaked(findingsText, "a finding");
        }

        // #18 and Issue 3 criterion 2: no raw value in a log line
        [Test]
        public void ScanAsync_OnTheFixture_PutsNoRawValueInALogLine()
        {
            IEnumerable<FakeLogRecord> records = _scannerLogger.Collector.GetSnapshot().Concat(_walkerLogger.Collector.GetSnapshot());
            string logText = string.Join("\n", records.Select(record => record.Message + " " + record.Exception));

            AssertNoValueLeaked(logText, "the log");
        }

        [Test]
        public void ScanAsync_OnTheFixture_SkipsTheToolFolderAndTheMarkedFolder()
        {
            FakeLogRecord summary = _walkerLogger.LatestRecord;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(summary.GetStructuredStateValue("FolderCount"), Is.EqualTo("1"), "node_modules");
                Assert.That(summary.GetStructuredStateValue("ExcludedCount"), Is.EqualTo("1"), "vendor");
            }
        }

        // Is.False with a message, not Does.Not.Contain: a failure must not print the value itself
        private static void AssertNoValueLeaked(string text, string place)
        {
            foreach (FixtureSecret secret in FixtureSecrets.All)
            {
                bool leaked = text.Contains(secret.Value, StringComparison.Ordinal);

                Assert.That(leaked, Is.False, secret.RuleId + " value from " + secret.FilePath + " leaked into " + place);
            }
        }
    }
}
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~FixtureScanTests" -p:CollectCoverage=false`
Expected: `Passed: 5`. If `FindsExactlyTheLabelledSecrets` fails, compare the fixture files with Step 1 character by character, especially line numbers and the fake values, before changing any rule.

- [ ] **Step 7: Run everything**

Run: `dotnet test RepoScanner.sln`
Expected: every project `Passed!`.

- [ ] **Step 8: Stop for review, then commit**

Show the user the diff and wait for approval. Then:
```bash
git add tests/fixtures tests/RepoScanner.Scanner.Tests/Secrets/.reposcanner-exclude tests/RepoScanner.Scanner.Tests/RepositoryRoot.cs tests/RepoScanner.Scanner.Tests/Secrets/FixtureSecret.cs tests/RepoScanner.Scanner.Tests/Secrets/FixtureSecrets.cs tests/RepoScanner.Scanner.Tests/Secrets/FixtureScanTests.cs
git commit -m "This commit adds the secrets-repo fixture with its markers, and tests that its secrets are found and never leaked #21 #18"
```

---

### Task 9: DatabaseOptions and the database registrations (#71)

**Files:**
- Create: `src/RepoScanner.Scanner/Configuration/DatabaseOptions.cs`
- Modify: `src/RepoScanner.Scanner/Program.cs` (usings; registrations in `CreateBuilder`)
- Create: `tests/RepoScanner.Scanner.Tests/ConsoleCapture.cs` (moved out of `ProgramTests`)
- Modify: `tests/RepoScanner.Scanner.Tests/ProgramTests.cs` (dummy connection string; use `ConsoleCapture`)
- Test: `tests/RepoScanner.Scanner.Tests/Configuration/DatabaseOptionsTests.cs`
- Test: `tests/RepoScanner.Scanner.Tests/Configuration/DatabaseRegistrationTests.cs`
- Modify: `README.md` (configuration table and the sentence below it)

**Interfaces:**
- Consumes: `ScannerDbContext`, `ScanRunRepository`, `FindingRepository` (Issue 2); `RepositoryRoot` (Task 8).
- Produces:
  - `public sealed record DatabaseOptions { public const string SectionName = "Scanner:Database"; [Required] public string ConnectionString { get; init; } }` in `RepoScanner.Scanner.Configuration`.
  - Registrations in `Program.CreateBuilder`: `DatabaseOptions` (validated on start), `ScannerDbContext` (scoped, SQL Server with retry), `IScanRunRepository` to `ScanRunRepository` and `IFindingRepository` to `FindingRepository` (scoped).
  - Test helper `internal static class ConsoleCapture { public static Task<string> RunAsync(Func<Task> action); }` in `RepoScanner.Scanner.Tests`.

- [ ] **Step 1: Move the console capture into its own helper**

`tests/RepoScanner.Scanner.Tests/ConsoleCapture.cs`:
```csharp
using System;
using System.IO;
using System.Threading.Tasks;

namespace RepoScanner.Scanner.Tests
{
    // Runs the given code with the console redirected, and returns everything it wrote. Serilog's
    // console sink writes to Console.Out, so this captures the scanner's log lines.
    internal static class ConsoleCapture
    {
        public static async Task<string> RunAsync(Func<Task> action)
        {
            TextWriter originalOut = Console.Out;

            using (StringWriter output = new StringWriter())
            {
                Console.SetOut(output);
                try
                {
                    await action();
                }
                finally
                {
                    Console.SetOut(originalOut);
                }

                return output.ToString();
            }
        }
    }
}
```

In `tests/RepoScanner.Scanner.Tests/ProgramTests.cs`:
- Delete the private `CaptureConsoleAsync` method and its comment (lines 114 to 133).
- Replace both calls `CaptureConsoleAsync(` with `ConsoleCapture.RunAsync(`.
- Remove `using System.IO;`, which is no longer needed.
- Replace the two argument arrays at the top with:
```csharp
        // Valid settings passed as command-line arguments, the last configuration source to be read.
        // Nothing connects to the database in queue mode, so its connection string only has to be present.
        private static readonly string[] ValidArgs =
        {
            "--Scanner:Queue:Name", "scan-test",
            "--Scanner:Queue:ConnectionString", "UseDevelopmentStorage=true",
            "--Scanner:Database:ConnectionString", "Server=unused;Database=unused"
        };

        // Settings with the required queue Name missing
        private static readonly string[] ArgsWithoutQueueName =
        {
            "--Scanner:Queue:ConnectionString", "UseDevelopmentStorage=true",
            "--Scanner:Database:ConnectionString", "Server=unused;Database=unused"
        };
```

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~ProgramTests" -p:CollectCoverage=false`
Expected: `Passed: 6`. The refactor changes no behaviour.

- [ ] **Step 2: Write the failing tests**

`tests/RepoScanner.Scanner.Tests/Configuration/DatabaseOptionsTests.cs`:
```csharp
using Microsoft.Extensions.Options;
using NUnit.Framework;
using RepoScanner.Scanner.Configuration;

namespace RepoScanner.Scanner.Tests.Configuration
{
    [TestFixture]
    public class DatabaseOptionsTests
    {
        // The same validator that ValidateDataAnnotations() registers in Program
        private readonly DataAnnotationValidateOptions<DatabaseOptions> _validator = new DataAnnotationValidateOptions<DatabaseOptions>(null);

        // #71: the message names the setting in full, so the fix is obvious from one log line
        [TestCase("")]
        [TestCase("   ")]
        public void Validate_WithoutConnectionString_FailsNamingTheSetting(string connectionString)
        {
            DatabaseOptions options = new DatabaseOptions { ConnectionString = connectionString };

            ValidateOptionsResult result = _validator.Validate(null, options);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.Failed, Is.True);
                Assert.That(result.FailureMessage, Does.Contain("Scanner:Database:ConnectionString"));
            }
        }

        [Test]
        public void Validate_WithConnectionString_Succeeds()
        {
            DatabaseOptions options = new DatabaseOptions { ConnectionString = "Server=.\\SQLEXPRESS;Database=RepoScanner;Trusted_Connection=True" };

            ValidateOptionsResult result = _validator.Validate(null, options);

            Assert.That(result.Succeeded, Is.True, result.FailureMessage);
        }
    }
}
```

`tests/RepoScanner.Scanner.Tests/Configuration/DatabaseRegistrationTests.cs`:
```csharp
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using RepoScanner.Core.Persistence;
using RepoScanner.Data;
using RepoScanner.Data.Repositories;

namespace RepoScanner.Scanner.Tests.Configuration
{
    [TestFixture]
    public class DatabaseRegistrationTests
    {
        private const string VariableName = "Scanner__Database__ConnectionString";

        private static readonly string[] QueueArgs =
        {
            "--Scanner:Queue:Name", "scan-test",
            "--Scanner:Queue:ConnectionString", "UseDevelopmentStorage=true"
        };

        // The developer's own variable (README: Database and migrations) is removed for each test and
        // put back afterwards, so "missing" really is missing on their machine too
        private string? _originalValue;

        [SetUp]
        public void SetUp()
        {
            _originalValue = Environment.GetEnvironmentVariable(VariableName);
            Environment.SetEnvironmentVariable(VariableName, null);
        }

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable(VariableName, _originalValue);
        }

        // #71 acceptance criterion: a missing connection string stops the scanner with exit code 2 and
        // one log line naming the setting
        [Test]
        public async Task Main_WithoutDatabaseConnectionString_ExitsTwoWithOneLineNamingTheSetting()
        {
            int exitCode = 0;
            string output = await ConsoleCapture.RunAsync(async () => { exitCode = await Program.Main(QueueArgs); });

            using (Assert.EnterMultipleScope())
            {
                Assert.That(exitCode, Is.EqualTo(2));
                Assert.That(output, Does.Contain("Scanner:Database:ConnectionString"));
                Assert.That(output.Split('\n').Count(line => line.Contains("FTL]", StringComparison.Ordinal)), Is.EqualTo(1));
                Assert.That(output, Does.Not.Contain("   at "), "A configuration error should not print a stack trace");
            }
        }

        // #71 acceptance criterion: the DbContext and both repositories resolve from the host's services,
        // and the context retries transient SQL errors (ADR-015). Resolving does not connect.
        [Test]
        public void CreateBuilder_ResolvesTheDbContextWithRetryAndBothRepositories()
        {
            string[] args = QueueArgs.Concat(new string[] { "--Scanner:Database:ConnectionString", "Server=unused;Database=unused" }).ToArray();

            using (IHost host = Program.CreateBuilder(args).Build())
            using (IServiceScope scope = host.Services.CreateScope())
            {
                ScannerDbContext context = scope.ServiceProvider.GetRequiredService<ScannerDbContext>();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(context.Database.ProviderName, Is.EqualTo("Microsoft.EntityFrameworkCore.SqlServer"));
                    Assert.That(context.Database.CreateExecutionStrategy(), Is.TypeOf<SqlServerRetryingExecutionStrategy>());
                    Assert.That(scope.ServiceProvider.GetRequiredService<IScanRunRepository>(), Is.TypeOf<ScanRunRepository>());
                    Assert.That(scope.ServiceProvider.GetRequiredService<IFindingRepository>(), Is.TypeOf<FindingRepository>());
                }
            }
        }

        // #71 acceptance criterion and ADR-007: no appsettings file holds a connection string
        [Test]
        public void AppSettingsFiles_HaveNoDatabaseSection()
        {
            string[] files = Directory.GetFiles(Path.Combine(RepositoryRoot.FullPath, "src", "RepoScanner.Scanner"), "appsettings*.json");
            Assert.That(files, Is.Not.Empty);

            foreach (string file in files)
            {
                IConfiguration configuration = new ConfigurationBuilder().AddJsonFile(file).Build();

                Assert.That(configuration.GetSection("Scanner:Database").Exists(), Is.False, Path.GetFileName(file));
            }
        }
    }
}
```

- [ ] **Step 3: Run them to make sure they fail**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~DatabaseOptionsTests|FullyQualifiedName~DatabaseRegistrationTests" -p:CollectCoverage=false`
Expected: build error `CS0246: The type or namespace name 'DatabaseOptions' could not be found`.

- [ ] **Step 4: Write DatabaseOptions**

`src/RepoScanner.Scanner/Configuration/DatabaseOptions.cs`:
```csharp
using System.ComponentModel.DataAnnotations;

namespace RepoScanner.Scanner.Configuration
{
    // The scanner's database connection, bound from the "Scanner:Database" configuration section and
    // validated when the host starts (ADR-022). Locally the value comes from the user environment
    // variable Scanner__Database__ConnectionString, the same one dotnet ef reads. No appsettings file
    // holds it (ADR-007).
    public sealed record DatabaseOptions
    {
        public const string SectionName = "Scanner:Database";

        // The message names the setting in full, so the one log line says exactly what to set.
        // [Required] also rejects an empty or whitespace-only value.
        [Required(ErrorMessage = "Scanner:Database:ConnectionString is required. Locally, set the Scanner__Database__ConnectionString user environment variable (README: Database and migrations).")]
        public string ConnectionString { get; init; } = string.Empty;
    }
}
```

- [ ] **Step 5: Register the database in Program**

In `src/RepoScanner.Scanner/Program.cs`, add these usings in order (System first, then alphabetical):
```csharp
using Microsoft.EntityFrameworkCore;
using RepoScanner.Core.Persistence;
using RepoScanner.Data;
using RepoScanner.Data.Repositories;
```

In `CreateBuilder`, after the `QueueOptions` registration and before `// Register the classes the host can create for us`, add:
```csharp
            // Database settings: read from the Scanner:Database section and checked when the host starts,
            // like the queue settings. Required in every mode: the scanner's job is to save findings.
            builder.Services.AddOptions<DatabaseOptions>()
                .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            // The shared EF Core context and repositories from RepoScanner.Data, one of each per scope.
            // The connection string comes from the validated settings, and a transient SQL error (such as
            // Azure SQL failing over) is retried rather than failing the scan (ADR-015). The repositories
            // use one SaveChanges per method and no explicit transactions, which retrying supports.
            builder.Services.AddDbContext<ScannerDbContext>((services, options) =>
            {
                DatabaseOptions database = services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
                options.UseSqlServer(database.ConnectionString, sql => sql.EnableRetryOnFailure());
            });
            builder.Services.AddScoped<IScanRunRepository, ScanRunRepository>();
            builder.Services.AddScoped<IFindingRepository, FindingRepository>();
```

- [ ] **Step 6: Run the tests to make sure they pass**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~Database|FullyQualifiedName~ProgramTests" -p:CollectCoverage=false`
Expected: `Passed: 12`.

- [ ] **Step 7: Update the README configuration table**

In `README.md`, after the `Scanner:Queue:MaxDequeueCount` row of the configuration table, add:
```markdown
| `Scanner:Database:ConnectionString` | Yes | | The findings database. Locally, the `Scanner__Database__ConnectionString` user environment variable (see [Database and migrations](#database-and-migrations)); never in an appsettings file (ADR-007) |
```

Replace the sentence
`Further settings (the scanner's database connection, Azure DevOps, execution limits) are added with the features that use them. The `dotnet ef` tool already reads the database connection string: see [Database and migrations](#database-and-migrations).`
with
`Further settings (Azure DevOps, execution limits) are added with the features that use them. The scanner and the `dotnet ef` tool read the same database connection string: see [Database and migrations](#database-and-migrations).`

- [ ] **Step 8: Run everything**

Run: `dotnet test RepoScanner.sln`
Expected: every project `Passed!`.

- [ ] **Step 9: Stop for review, then commit**

Show the user the diff and wait for approval. Then:
```bash
git add src/RepoScanner.Scanner/Configuration/DatabaseOptions.cs src/RepoScanner.Scanner/Program.cs tests/RepoScanner.Scanner.Tests/ConsoleCapture.cs tests/RepoScanner.Scanner.Tests/ProgramTests.cs tests/RepoScanner.Scanner.Tests/Configuration/DatabaseOptionsTests.cs tests/RepoScanner.Scanner.Tests/Configuration/DatabaseRegistrationTests.cs README.md
git commit -m "This commit adds DatabaseOptions and registers the DbContext with SQL retry and the repositories in the scanner #71"
```

---

### Task 10: LocalScanRunner (#20)

**Files:**
- Create: `src/RepoScanner.Scanner/Local/LocalScanRunner.cs`
- Modify: `tests/RepoScanner.Scanner.Tests/RepoScanner.Scanner.Tests.csproj` (add SQLite)
- Create: `tests/RepoScanner.Scanner.Tests/Local/SqliteTestDatabase.cs`
- Create: `tests/RepoScanner.Scanner.Tests/Local/StubScanner.cs`
- Test: `tests/RepoScanner.Scanner.Tests/Local/LocalScanRunnerTests.cs`

**Interfaces:**
- Consumes: `IScanner`, `RawFinding` (Task 2); `IScanRunRepository`, `IFindingRepository`, `RepositoryDetails`, `ScanRunCompletion`, `FindingChanges`, entities and enums (Issue 2).
- Produces:
  - `public sealed class LocalScanRunner` in `RepoScanner.Scanner.Local`: `public const string ScannerActor = "scanner"`, `public const string LocalProjectName = "Local"`, `public const string LocalBranchName = "local"`; constructor `LocalScanRunner(IScanRunRepository scanRuns, IFindingRepository findings, IEnumerable<IScanner> scanners, ILogger<LocalScanRunner> logger)`; `public Task RunAsync(string folder, CancellationToken cancellationToken)`; `public static RepositoryDetails DescribeFolder(string folder)`.
  - Test helpers in `RepoScanner.Scanner.Tests.Local`: `internal sealed class SqliteTestDatabase : IDisposable` (`DbContextOptions<ScannerDbContext> Options`, `ScannerDbContext CreateContext()`); `internal sealed class StubScanner : IScanner` (`StubScanner(params RawFinding[] findings)`, `StubScanner(Exception failure)`).

- [ ] **Step 1: Add SQLite to the Scanner tests**

In `tests/RepoScanner.Scanner.Tests/RepoScanner.Scanner.Tests.csproj`, inside the `ItemGroup` of package references, after `Microsoft.Extensions.Diagnostics.Testing`, add:
```xml
    <!-- In-memory SQLite database, in place of SQL Server, for the local mode tests -->
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" />
```
The version (10.0.12) is already in `Directory.Packages.props`.

- [ ] **Step 2: Write the test helpers**

`tests/RepoScanner.Scanner.Tests/Local/SqliteTestDatabase.cs`:
```csharp
using System;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RepoScanner.Data;

namespace RepoScanner.Scanner.Tests.Local
{
    // A new, empty SQLite database in memory for one test, as in RepoScanner.Data.Tests. It exists only
    // while its connection is open, so the connection stays open and every context shares it.
    internal sealed class SqliteTestDatabase : IDisposable
    {
        private readonly SqliteConnection _connection;

        public SqliteTestDatabase()
        {
            // SQLite only enforces foreign keys when asked to
            _connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
            _connection.Open();
            Options = new DbContextOptionsBuilder<ScannerDbContext>().UseSqlite(_connection).Options;

            using (ScannerDbContext context = CreateContext())
            {
                // Test projects may build the schema this way; the scanner never does (ADR-006, task #13)
                context.Database.EnsureCreated();
            }
        }

        public DbContextOptions<ScannerDbContext> Options { get; }

        // A new context each time, so a test reads back what was saved from the database itself
        public ScannerDbContext CreateContext()
        {
            return new ScannerDbContext(Options);
        }

        public void Dispose()
        {
            _connection.Dispose();
        }
    }
}
```

`tests/RepoScanner.Scanner.Tests/Local/StubScanner.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RepoScanner.Core.Enums;
using RepoScanner.Core.Scanning;

namespace RepoScanner.Scanner.Tests.Local
{
    // A scanner that returns the findings it was given, or fails, so LocalScanRunner can be tested
    // without reading any files
    internal sealed class StubScanner : IScanner
    {
        private readonly IReadOnlyList<RawFinding> _findings;
        private readonly Exception? _failure;

        public StubScanner(params RawFinding[] findings)
        {
            _findings = findings;
        }

        public StubScanner(Exception failure)
        {
            _findings = Array.Empty<RawFinding>();
            _failure = failure;
        }

        public ScannerType ScannerType => ScannerType.Secret;

        public Task<IReadOnlyList<RawFinding>> ScanAsync(string rootPath, CancellationToken cancellationToken)
        {
            return _failure == null
                ? Task.FromResult(_findings)
                : Task.FromException<IReadOnlyList<RawFinding>>(_failure);
        }
    }
}
```

- [ ] **Step 3: Write the failing tests**

`tests/RepoScanner.Scanner.Tests/Local/LocalScanRunnerTests.cs`:
```csharp
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Testing;
using NUnit.Framework;
using RepoScanner.Core.Entities;
using RepoScanner.Core.Enums;
using RepoScanner.Core.Scanning;
using RepoScanner.Data;
using RepoScanner.Data.Repositories;
using RepoScanner.Scanner.Local;

namespace RepoScanner.Scanner.Tests.Local
{
    [TestFixture]
    public class LocalScanRunnerTests
    {
        // The runner never reads this folder itself: the stub scanner stands in for the real one
        private static readonly string Folder = Path.Combine(Path.GetTempPath(), "orders-api");

        private SqliteTestDatabase _database = null!;
        private FakeLogger<LocalScanRunner> _logger = null!;

        [SetUp]
        public void SetUp()
        {
            _database = new SqliteTestDatabase();
            _logger = new FakeLogger<LocalScanRunner>();
        }

        [TearDown]
        public void TearDown()
        {
            _database.Dispose();
        }

        [Test]
        public async Task RunAsync_AddsTheFolderAsALocalRepository()
        {
            await RunAsync(new StubScanner());

            using (ScannerDbContext context = _database.CreateContext())
            {
                Repository repository = context.Repositories.Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(repository.ProjectName, Is.EqualTo("Local"));
                    Assert.That(repository.Name, Is.EqualTo("orders-api"));
                    Assert.That(repository.DefaultBranch, Is.EqualTo("local"));
                    Assert.That(repository.AzureDevOpsId, Is.EqualTo(LocalScanRunner.DescribeFolder(Folder).AzureDevOpsId));
                }
            }
        }

        [Test]
        public async Task RunAsync_SavesEachFindingAsOpenWithADetectedEvent()
        {
            await RunAsync(new StubScanner(Raw("SEC003", "src/appsettings.json", 3, "hash-a")));

            using (ScannerDbContext context = _database.CreateContext())
            {
                Finding finding = context.Findings.Include(saved => saved.Events).Single();
                ScanRun run = context.ScanRuns.Single();
                FindingEvent detected = finding.Events.Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(finding.RuleId, Is.EqualTo("SEC003"));
                    Assert.That(finding.FilePath, Is.EqualTo("src/appsettings.json"));
                    Assert.That(finding.LineNumber, Is.EqualTo(3));
                    Assert.That(finding.MaskedPreview, Is.EqualTo("FAKE****"));
                    Assert.That(finding.SecretHash, Is.EqualTo("hash-a"));
                    Assert.That(finding.Status, Is.EqualTo(FindingStatus.Open));
                    Assert.That(finding.Fingerprint, Does.Match("^[0-9a-f]{64}$"));
                    Assert.That(finding.LastSeenUtc, Is.EqualTo(finding.FirstSeenUtc));
                    Assert.That(detected.EventType, Is.EqualTo(FindingEventType.Detected));
                    Assert.That(detected.Actor, Is.EqualTo("scanner"));
                    Assert.That(detected.ScanRunId, Is.EqualTo(run.Id));
                }
            }
        }

        [Test]
        public async Task RunAsync_CompletesTheRunWithTheNewCountAndOneScannerResult()
        {
            await RunAsync(new StubScanner(Raw("SEC003", "a.json", 3, "hash-a"), Raw("SEC007", "a.json", 6, "hash-b")));

            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun run = context.ScanRuns.Include(saved => saved.ScannerResults).Single();
                ScannerResult result = run.ScannerResults.Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(run.Status, Is.EqualTo(ScanRunStatus.Completed));
                    Assert.That(run.TriggerType, Is.EqualTo(ScanTrigger.OnDemand));
                    Assert.That(run.NewCount, Is.EqualTo(2));
                    Assert.That(run.ReopenedCount, Is.Zero);
                    Assert.That(run.ResolvedCount, Is.Zero);
                    Assert.That(run.CommitSha, Is.Null);
                    Assert.That(run.CompletedUtc, Is.Not.Null);
                    Assert.That(result.ScannerType, Is.EqualTo(ScannerType.Secret));
                    Assert.That(result.Status, Is.EqualTo(ScannerStatus.Succeeded));
                    Assert.That(result.FindingCount, Is.EqualTo(2));
                }
            }
        }

        // Design decision 2 (interim): a second run of the same folder adds nothing, rather than failing
        // on the unique fingerprint
        [Test]
        public async Task RunAsync_OnTheSameFolderTwice_AddsNothingTheSecondTime()
        {
            RawFinding raw = Raw("SEC003", "src/appsettings.json", 3, "hash-a");

            await RunAsync(new StubScanner(raw));
            await RunAsync(new StubScanner(raw));

            using (ScannerDbContext context = _database.CreateContext())
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(context.Findings.Count(), Is.EqualTo(1));
                    Assert.That(context.Repositories.Count(), Is.EqualTo(1));
                    Assert.That(context.ScanRuns.OrderBy(run => run.Id).Select(run => run.NewCount), Is.EqualTo(new int[] { 1, 0 }));
                    Assert.That(context.ScanRuns.Select(run => run.Status), Is.All.EqualTo(ScanRunStatus.Completed));
                }
            }
        }

        // The fingerprint has no line number, so the same secret twice in one file is one finding
        [Test]
        public async Task RunAsync_WithTheSameSecretTwiceInOneFile_SavesOneFinding()
        {
            await RunAsync(new StubScanner(Raw("SEC007", "a.json", 2, "hash-a"), Raw("SEC007", "a.json", 9, "hash-a")));

            using (ScannerDbContext context = _database.CreateContext())
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(context.Findings.Single().LineNumber, Is.EqualTo(2));
                    Assert.That(context.ScanRuns.Single().NewCount, Is.EqualTo(1));
                }
            }
        }

        // ADR-013: a failed scan is always visible in ScanRuns
        [Test]
        public async Task RunAsync_WhenAScannerFails_MarksTheRunFailedAndRethrows()
        {
            await Assert.ThatAsync(() => RunAsync(new StubScanner(new InvalidOperationException("Simulated failure"))),
                Throws.InvalidOperationException);

            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun run = context.ScanRuns.Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(run.Status, Is.EqualTo(ScanRunStatus.Failed));
                    Assert.That(run.ErrorMessage, Is.EqualTo("Simulated failure"));
                    Assert.That(run.CompletedUtc, Is.Not.Null);
                }
            }
        }

        // Log lines name the rule, file and line: never the preview, or anything else from the match
        [Test]
        public async Task RunAsync_LogsEachNewFindingWithoutItsPreview()
        {
            await RunAsync(new StubScanner(Raw("SEC003", "src/appsettings.json", 3, "hash-a")));

            string line = _logger.Collector.GetSnapshot()
                .Select(record => record.Message)
                .Single(message => message.StartsWith("New finding", StringComparison.Ordinal));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(line, Is.EqualTo("New finding SEC003 in src/appsettings.json at line 3"));
                Assert.That(line, Does.Not.Contain("FAKE****"));
            }
        }

        // Review focus: however the folder is written, it is the same Repositories row
        [Test]
        public void DescribeFolder_GivesTheSameIdHoweverTheFolderIsWritten()
        {
            Guid id = LocalScanRunner.DescribeFolder(Folder).AzureDevOpsId;
            string relative = Path.GetRelativePath(Environment.CurrentDirectory, Folder);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(LocalScanRunner.DescribeFolder(Folder + Path.DirectorySeparatorChar).AzureDevOpsId, Is.EqualTo(id));
                Assert.That(LocalScanRunner.DescribeFolder(relative).AzureDevOpsId, Is.EqualTo(id));
                Assert.That(LocalScanRunner.DescribeFolder(Folder.ToUpperInvariant()).AzureDevOpsId, Is.EqualTo(id));
            }
        }

        [Test]
        public void DescribeFolder_GivesDifferentFoldersDifferentIds()
        {
            Guid first = LocalScanRunner.DescribeFolder(Folder).AzureDevOpsId;
            Guid second = LocalScanRunner.DescribeFolder(Folder + "-copy").AzureDevOpsId;

            Assert.That(second, Is.Not.EqualTo(first));
        }

        private async Task RunAsync(IScanner scanner)
        {
            using (ScannerDbContext context = _database.CreateContext())
            {
                LocalScanRunner runner = new LocalScanRunner(new ScanRunRepository(context), new FindingRepository(context), new IScanner[] { scanner }, _logger);

                await runner.RunAsync(Folder, CancellationToken.None);
            }
        }

        // A secret finding as SecretScanner would report it; the hash stands in for a real one
        private static RawFinding Raw(string ruleId, string filePath, int lineNumber, string secretHash)
        {
            return new RawFinding
            {
                ScannerType = ScannerType.Secret,
                RuleId = ruleId,
                Severity = Severity.High,
                FilePath = filePath,
                LineNumber = lineNumber,
                Message = "Test finding",
                MaskedPreview = "FAKE****",
                SecretHash = secretHash
            };
        }
    }
}
```

- [ ] **Step 4: Run them to make sure they fail**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~LocalScanRunnerTests" -p:CollectCoverage=false`
Expected: build error `CS0234: The type or namespace name 'Local' does not exist in the namespace 'RepoScanner.Scanner'`.

- [ ] **Step 5: Write the runner**

`src/RepoScanner.Scanner/Local/LocalScanRunner.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RepoScanner.Core.Entities;
using RepoScanner.Core.Enums;
using RepoScanner.Core.Persistence;
using RepoScanner.Core.Scanning;

namespace RepoScanner.Scanner.Local
{
    // Runs the scanners on a folder on this machine and saves what they find (--path, #20): the first
    // vertical slice, with no queue and no clone. The queue loop arrives in Issue 5.
    public sealed class LocalScanRunner
    {
        // Events the scanner raises are attributed to it rather than to a person (Issue 2 design)
        public const string ScannerActor = "scanner";

        // Local folders are kept under this project name, so they are easy to tell apart from Azure
        // DevOps repositories in the Repositories table
        public const string LocalProjectName = "Local";

        public const string LocalBranchName = "local";

        private readonly IScanRunRepository _scanRuns;
        private readonly IFindingRepository _findings;
        private readonly IReadOnlyList<IScanner> _scanners;
        private readonly ILogger<LocalScanRunner> _logger;

        public LocalScanRunner(IScanRunRepository scanRuns, IFindingRepository findings, IEnumerable<IScanner> scanners, ILogger<LocalScanRunner> logger)
        {
            _scanRuns = scanRuns;
            _findings = findings;

            // Every registered scanner: only SecretScanner in Issue 3, all three once Issues 7 and 8
            // register theirs. Asking for a single IScanner would silently get the last one registered.
            _scanners = scanners.ToList();
            _logger = logger;
        }

        // A local folder has no Azure DevOps ID, so it gets one made from its full path: the first
        // 16 bytes of the SHA-256 of the lower-cased path. The same folder always maps to the same
        // Repositories row, however it is written (design decision 11).
        public static RepositoryDetails DescribeFolder(string folder)
        {
            string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(fullPath.ToLowerInvariant()));
            string name = Path.GetFileName(fullPath);

            // A drive root such as C:\ has no folder name, so its path is used instead
            return new RepositoryDetails(new Guid(hash.AsSpan(0, 16)), LocalProjectName, name.Length > 0 ? name : fullPath, LocalBranchName);
        }

        public async Task RunAsync(string folder, CancellationToken cancellationToken)
        {
            RepositoryDetails repository = DescribeFolder(folder);
            ScanRun scanRun = await _scanRuns.StartRunAsync(repository, ScanTrigger.OnDemand, Guid.NewGuid(), DateTime.UtcNow, cancellationToken);
            _logger.LogInformation("Scanning {Folder} as scan run {ScanRunId}", folder, scanRun.Id);

            Stopwatch duration = Stopwatch.StartNew();
            try
            {
                List<RawFinding> found = new List<RawFinding>();
                List<ScannerResult> results = new List<ScannerResult>();

                foreach (IScanner scanner in _scanners)
                {
                    Stopwatch scannerDuration = Stopwatch.StartNew();
                    IReadOnlyList<RawFinding> scannerFindings = await scanner.ScanAsync(folder, cancellationToken);

                    results.Add(new ScannerResult
                    {
                        ScannerType = scanner.ScannerType,
                        Status = ScannerStatus.Succeeded,
                        FindingCount = scannerFindings.Count,
                        DurationMs = (int)scannerDuration.ElapsedMilliseconds
                    });
                    found.AddRange(scannerFindings);
                }

                int newCount = await SaveNewFindingsAsync(scanRun, found, cancellationToken);

                ScanRunCompletion completion = new ScanRunCompletion(
                    ScanRunStatus.Completed, CommitSha: null, DateTime.UtcNow, newCount, ReopenedCount: 0, ResolvedCount: 0, ErrorMessage: null, results);
                await _scanRuns.CompleteRunAsync(scanRun.Id, completion, cancellationToken);

                _logger.LogInformation("Scan run {ScanRunId} completed in {DurationMs} ms: {FoundCount} found, {NewCount} new, {KnownCount} already stored",
                    scanRun.Id, duration.ElapsedMilliseconds, found.Count, newCount, found.Count - newCount);
            }
            catch (Exception exception)
            {
                await RecordFailureAsync(scanRun.Id, exception);
                throw;
            }
        }

        // Interim, Issue 3 only (design decision 2): findings already stored, or repeated within this
        // run, are skipped, so running the same folder twice adds nothing. Replaced by
        // FingerprintCalculator (#23) and FindingReconciler (#24) when #26 wires them into local mode.
        private async Task<int> SaveNewFindingsAsync(ScanRun scanRun, IReadOnlyList<RawFinding> found, CancellationToken cancellationToken)
        {
            IReadOnlyList<Finding> stored = await _findings.GetByRepositoryAsync(scanRun.RepositoryId, cancellationToken);
            HashSet<string> seen = new HashSet<string>(stored.Select(finding => finding.Fingerprint), StringComparer.Ordinal);
            DateTime nowUtc = DateTime.UtcNow;
            List<Finding> newFindings = new List<Finding>();

            foreach (RawFinding raw in found)
            {
                string fingerprint = InterimFingerprint(raw);

                // False when the fingerprint is already stored, or was already added in this run
                if (!seen.Add(fingerprint))
                {
                    continue;
                }

                Finding finding = ToFinding(raw, scanRun.RepositoryId, fingerprint, nowUtc);
                finding.Events.Add(new FindingEvent
                {
                    ScanRunId = scanRun.Id,
                    EventType = FindingEventType.Detected,
                    OccurredUtc = nowUtc,
                    Actor = ScannerActor
                });
                newFindings.Add(finding);

                // The rule, file and line only: never the matched text or its preview
                _logger.LogInformation("New finding {RuleId} in {FilePath} at line {LineNumber}", raw.RuleId, raw.FilePath, raw.LineNumber);
            }

            await _findings.SaveChangesAsync(new FindingChanges(newFindings, Array.Empty<Finding>(), Array.Empty<FindingEvent>()), cancellationToken);
            return newFindings.Count;
        }

        // SHA-256, in lower-case hex, of the rule, the file and the secret's hash. No line number, so a
        // finding that moves down the file keeps its fingerprint, as #27 will require.
        private static string InterimFingerprint(RawFinding raw)
        {
            string identity = raw.RuleId + "|" + raw.FilePath + "|" + raw.SecretHash;
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        }

        private static Finding ToFinding(RawFinding raw, int repositoryId, string fingerprint, DateTime nowUtc)
        {
            return new Finding
            {
                RepositoryId = repositoryId,
                Fingerprint = fingerprint,
                ScannerType = raw.ScannerType,
                RuleId = raw.RuleId,
                Severity = raw.Severity,
                FilePath = raw.FilePath,
                LineNumber = raw.LineNumber,
                Message = raw.Message,
                MaskedPreview = raw.MaskedPreview,
                SecretHash = raw.SecretHash,
                PackageName = raw.PackageName,
                PackageVersion = raw.PackageVersion,
                AdvisoryUrl = raw.AdvisoryUrl,
                Status = FindingStatus.Open,
                FirstSeenUtc = nowUtc,
                LastSeenUtc = nowUtc
            };
        }

        // Marks the run Failed with the error, so a failed scan is visible in ScanRuns (ADR-013). If that
        // fails too, for example because the database is down, it is logged and the original error is
        // the one that continues.
        private async Task RecordFailureAsync(int scanRunId, Exception failure)
        {
            try
            {
                ScanRunCompletion completion = new ScanRunCompletion(
                    ScanRunStatus.Failed, CommitSha: null, DateTime.UtcNow, NewCount: 0, ReopenedCount: 0, ResolvedCount: 0, failure.Message, Array.Empty<ScannerResult>());

                // Not the run's token: a cancelled run is still recorded as Failed
                await _scanRuns.CompleteRunAsync(scanRunId, completion, CancellationToken.None);
            }
            catch (Exception recordingFailure)
            {
                _logger.LogWarning(recordingFailure, "Could not mark scan run {ScanRunId} as failed", scanRunId);
            }
        }
    }
}
```

- [ ] **Step 6: Run them to make sure they pass**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~LocalScanRunnerTests" -p:CollectCoverage=false`
Expected: `Passed: 9`.

- [ ] **Step 7: Run everything**

Run: `dotnet test RepoScanner.sln`
Expected: every project `Passed!`, Scanner coverage at 80% or more.

- [ ] **Step 8: Stop for review, then commit**

Show the user the diff and wait for approval. Then:
```bash
git add src/RepoScanner.Scanner/Local/LocalScanRunner.cs tests/RepoScanner.Scanner.Tests/RepoScanner.Scanner.Tests.csproj tests/RepoScanner.Scanner.Tests/Local
git commit -m "This commit adds LocalScanRunner, which scans a folder and saves its new findings with an interim fingerprint #20"
```

---

### Task 11: The --path local mode, end to end (#20)

**Files:**
- Create: `src/RepoScanner.Scanner/Local/LocalPathArgument.cs`
- Modify: `src/RepoScanner.Scanner/Program.cs` (full new version below)
- Modify: `tests/RepoScanner.Scanner.Tests/Local/SqliteTestDatabase.cs` (add `ReplaceDatabaseIn`)
- Modify: `tests/RepoScanner.Scanner.Tests/ProgramTests.cs` (two new tests)
- Test: `tests/RepoScanner.Scanner.Tests/Local/LocalPathArgumentTests.cs`
- Test: `tests/RepoScanner.Scanner.Tests/Local/LocalModeTests.cs`
- Modify: `README.md` (new section, exit codes, repository layout)

**Interfaces:**
- Consumes: everything above.
- Produces: `public static class LocalPathArgument { public const string Name = "--path"; public static bool TryGet(string[] args, out string? folder); }` in `RepoScanner.Scanner.Local`; `SqliteTestDatabase.ReplaceDatabaseIn(IServiceCollection services)`.

- [ ] **Step 1: Write the failing argument tests**

`tests/RepoScanner.Scanner.Tests/Local/LocalPathArgumentTests.cs`:
```csharp
using System;
using NUnit.Framework;
using RepoScanner.Scanner.Local;

namespace RepoScanner.Scanner.Tests.Local
{
    [TestFixture]
    public class LocalPathArgumentTests
    {
        [TestCase(new string[] { "--path", "src" })]
        [TestCase(new string[] { "--path=src" })]
        [TestCase(new string[] { "--PATH", "src" })]
        [TestCase(new string[] { "--Scanner:Database:ConnectionString", "x", "--path", "src" })]
        public void TryGet_WithAFolder_ReturnsIt(string[] args)
        {
            bool found = LocalPathArgument.TryGet(args, out string? folder);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(found, Is.True);
                Assert.That(folder, Is.EqualTo("src"));
            }
        }

        // --path is there but names no folder: an argument starting "--" is another setting
        [TestCase(new string[] { "--path" })]
        [TestCase(new string[] { "--path=" })]
        [TestCase(new string[] { "--path", "--Scanner:Queue:Name", "scan-test" })]
        public void TryGet_WithoutAFolder_ReturnsTrueAndNull(string[] args)
        {
            bool found = LocalPathArgument.TryGet(args, out string? folder);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(found, Is.True);
                Assert.That(folder, Is.Null);
            }
        }

        [TestCase(new string[] { "--Scanner:Queue:Name", "scan-test" })]
        [TestCase(new string[] { "--pathway", "src" })]
        public void TryGet_WithoutPath_ReturnsFalse(string[] args)
        {
            Assert.That(LocalPathArgument.TryGet(args, out _), Is.False);
        }

        [Test]
        public void TryGet_WithNoArguments_ReturnsFalse()
        {
            Assert.That(LocalPathArgument.TryGet(Array.Empty<string>(), out _), Is.False);
        }
    }
}
```

- [ ] **Step 2: Run them to make sure they fail**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~LocalPathArgumentTests" -p:CollectCoverage=false`
Expected: build error `CS0103: The name 'LocalPathArgument' does not exist in the current context`.

- [ ] **Step 3: Write LocalPathArgument**

`src/RepoScanner.Scanner/Local/LocalPathArgument.cs`:
```csharp
using System;

namespace RepoScanner.Scanner.Local
{
    // Finds --path <folder> or --path=<folder> in the command-line arguments (#20). Read from the
    // arguments rather than IConfiguration: the host adds every environment variable to configuration,
    // so Windows' own PATH variable would appear as the setting "path" on every run (design decision 9).
    public static class LocalPathArgument
    {
        public const string Name = "--path";

        // True when --path is present. folder is what follows it, or null when nothing does; an
        // argument starting "--" is another setting, not a folder.
        public static bool TryGet(string[] args, out string? folder)
        {
            for (int index = 0; index < args.Length; index++)
            {
                string argument = args[index];

                if (string.Equals(argument, Name, StringComparison.OrdinalIgnoreCase))
                {
                    bool hasValue = index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal);
                    folder = hasValue ? args[index + 1] : null;
                    return true;
                }

                if (argument.StartsWith(Name + "=", StringComparison.OrdinalIgnoreCase))
                {
                    string value = argument.Substring(Name.Length + 1);
                    folder = value.Length > 0 ? value : null;
                    return true;
                }
            }

            folder = null;
            return false;
        }
    }
}
```

- [ ] **Step 4: Run them to make sure they pass**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~LocalPathArgumentTests" -p:CollectCoverage=false`
Expected: `Passed: 10`.

- [ ] **Step 5: Let the tests swap SQL Server for SQLite**

In `tests/RepoScanner.Scanner.Tests/Local/SqliteTestDatabase.cs`, add these usings:
```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
```
and this method after `CreateContext`:
```csharp
        // Swaps the scanner's SQL Server registration for this database, for tests that run the whole
        // program. The context is built from DbContextOptions<ScannerDbContext>, so replacing that one
        // registration is enough.
        public void ReplaceDatabaseIn(IServiceCollection services)
        {
            services.RemoveAll<DbContextOptions<ScannerDbContext>>();
            services.AddScoped<DbContextOptions<ScannerDbContext>>(_ => Options);
        }
```

- [ ] **Step 6: Write the failing end-to-end tests**

`tests/RepoScanner.Scanner.Tests/Local/LocalModeTests.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using RepoScanner.Core.Entities;
using RepoScanner.Core.Enums;
using RepoScanner.Data;
using RepoScanner.Scanner.Secrets;
using RepoScanner.Scanner.Tests.Secrets;

namespace RepoScanner.Scanner.Tests.Local
{
    // The whole program in local mode, with in-memory SQLite in place of SQL Server
    [TestFixture]
    public class LocalModeTests
    {
        // Satisfies the start-up check; SQL Server is swapped out, so nothing ever connects to it
        private static readonly string[] DatabaseArgs = { "--Scanner:Database:ConnectionString", "Server=unused;Database=unused" };

        private SqliteTestDatabase _database = null!;

        [SetUp]
        public void SetUp()
        {
            _database = new SqliteTestDatabase();
        }

        [TearDown]
        public void TearDown()
        {
            _database.Dispose();
        }

        // Issue 3 acceptance criterion 1: running against the fixture writes the expected findings to
        // SQL with masked previews
        [Test]
        public async Task Path_OnTheFixture_SavesTheLabelledSecretsWithMaskedPreviews()
        {
            (int exitCode, _) = await RunAsync("--path", FixtureSecrets.FolderPath);

            List<Finding> findings = SavedFindings();
            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(findings.Select(finding => (finding.RuleId, finding.FilePath, finding.LineNumber)),
                Is.EqualTo(FixtureSecrets.All.Select(secret => (secret.RuleId, secret.FilePath, (int?)secret.LineNumber))));

            for (int index = 0; index < findings.Count; index++)
            {
                FixtureSecret secret = FixtureSecrets.All[index];
                string label = secret.RuleId + " in " + secret.FilePath;

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(findings[index].MaskedPreview, Is.EqualTo(SecretMasker.Mask(secret.Value)), label);
                    Assert.That(findings[index].SecretHash, Is.EqualTo(SecretHasher.Hash(secret.Value)), label);
                    Assert.That(findings[index].Status, Is.EqualTo(FindingStatus.Open), label);
                    Assert.That(findings[index].Events.Single().EventType, Is.EqualTo(FindingEventType.Detected), label);
                }
            }
        }

        [Test]
        public async Task Path_OnTheFixture_CompletesTheRunWithOneSucceededSecretScannerResult()
        {
            await RunAsync("--path", FixtureSecrets.FolderPath);

            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun run = context.ScanRuns.Include(saved => saved.ScannerResults).Single();
                ScannerResult result = run.ScannerResults.Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(run.Status, Is.EqualTo(ScanRunStatus.Completed));
                    Assert.That(run.NewCount, Is.EqualTo(FixtureSecrets.All.Count));
                    Assert.That(result.ScannerType, Is.EqualTo(ScannerType.Secret));
                    Assert.That(result.Status, Is.EqualTo(ScannerStatus.Succeeded));
                    Assert.That(result.FindingCount, Is.EqualTo(FixtureSecrets.All.Count));
                    Assert.That(context.Repositories.Single().ProjectName, Is.EqualTo("Local"));
                }
            }
        }

        // Design decision 2: the demo can be run as often as needed
        [Test]
        public async Task Path_OnTheFixtureTwice_AddsNothingTheSecondTime()
        {
            await RunAsync("--path", FixtureSecrets.FolderPath);
            (int exitCode, _) = await RunAsync("--path", FixtureSecrets.FolderPath);

            using (ScannerDbContext context = _database.CreateContext())
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(exitCode, Is.EqualTo(0));
                    Assert.That(context.Findings.Count(), Is.EqualTo(FixtureSecrets.All.Count));
                    Assert.That(context.ScanRuns.OrderBy(run => run.Id).Select(run => run.NewCount),
                        Is.EqualTo(new int[] { FixtureSecrets.All.Count, 0 }));
                }
            }
        }

        // Issue 3 acceptance criterion 2: no raw value in the saved rows or in the log
        [Test]
        public async Task Path_OnTheFixture_PutsNoRawValueInTheDatabaseOrTheLog()
        {
            (_, string output) = await RunAsync("--path", FixtureSecrets.FolderPath);

            string savedText;
            using (ScannerDbContext context = _database.CreateContext())
            {
                IEnumerable<string> findingRows = context.Findings.AsEnumerable().Select(finding =>
                    string.Join("|", finding.Fingerprint, finding.RuleId, finding.FilePath, finding.Message, finding.MaskedPreview, finding.SecretHash));
                IEnumerable<string> runRows = context.ScanRuns.AsEnumerable().Select(run => run.ErrorMessage ?? string.Empty);
                savedText = string.Join("\n", findingRows.Concat(runRows));
            }

            foreach (FixtureSecret secret in FixtureSecrets.All)
            {
                // Is.False with a message, not Does.Not.Contain: a failure must not print the value itself
                bool inDatabase = savedText.Contains(secret.Value, StringComparison.Ordinal);
                bool inLog = output.Contains(secret.Value, StringComparison.Ordinal);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(inDatabase, Is.False, secret.RuleId + " value from " + secret.FilePath + " leaked into the database");
                    Assert.That(inLog, Is.False, secret.RuleId + " value from " + secret.FilePath + " leaked into the log");
                }
            }
        }

        [Test]
        public async Task Path_ToAFolderThatDoesNotExist_ExitsTwoWithOneLine()
        {
            (int exitCode, string output) = await RunAsync("--path", Path.Combine(FixtureSecrets.FolderPath, "does-not-exist"));

            AssertExitTwoWithOneLine(exitCode, output);
        }

        [Test]
        public async Task Path_WithoutAFolder_ExitsTwoWithOneLine()
        {
            (int exitCode, string output) = await RunAsync("--path");

            AssertExitTwoWithOneLine(exitCode, output);
        }

        // Review focus: a file is not a folder
        [Test]
        public async Task Path_ToAFile_ExitsTwoWithOneLine()
        {
            (int exitCode, string output) = await RunAsync("--path", Path.Combine(FixtureSecrets.FolderPath, "README.md"));

            AssertExitTwoWithOneLine(exitCode, output);
        }

        private async Task<(int ExitCode, string Output)> RunAsync(params string[] args)
        {
            int exitCode = 0;
            string output = await ConsoleCapture.RunAsync(async () =>
            {
                exitCode = await Program.RunAsync(args.Concat(DatabaseArgs).ToArray(), services => _database.ReplaceDatabaseIn(services));
            });

            return (exitCode, output);
        }

        // Sorted in memory by ordinal, the order FixtureSecrets lists them in
        private List<Finding> SavedFindings()
        {
            using (ScannerDbContext context = _database.CreateContext())
            {
                return context.Findings.Include(finding => finding.Events).AsEnumerable()
                    .OrderBy(finding => finding.FilePath, StringComparer.Ordinal)
                    .ThenBy(finding => finding.LineNumber)
                    .ToList();
            }
        }

        private static void AssertExitTwoWithOneLine(int exitCode, string output)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(exitCode, Is.EqualTo(2));
                Assert.That(output.Split('\n').Count(line => line.Contains("FTL]", StringComparison.Ordinal)), Is.EqualTo(1));
                Assert.That(output, Does.Contain("--path"));
                Assert.That(output, Does.Not.Contain("   at "), "A settings problem should not print a stack trace");
            }
        }
    }
}
```

Add to `tests/RepoScanner.Scanner.Tests/ProgramTests.cs` (with the usings `System.Collections.Generic`, `System.Linq`, `RepoScanner.Core.Scanning`, `RepoScanner.Scanner.Local` and `RepoScanner.Scanner.Secrets`):
```csharp
        [Test]
        public void CreateBuilder_RegistersTheSecretScannerWithAllSevenRules()
        {
            using (IHost host = Program.CreateBuilder(ValidArgs).Build())
            {
                IEnumerable<string> ruleIds = host.Services.GetServices<ISecretRule>().Select(rule => rule.RuleId);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(ruleIds, Is.EquivalentTo(new string[] { "SEC001", "SEC002", "SEC003", "SEC004", "SEC005", "SEC006", "SEC007" }));
                    Assert.That(host.Services.GetServices<IScanner>().Single(), Is.TypeOf<SecretScanner>());
                }
            }
        }

        // Local mode reads no queue, so it starts without queue settings (#20, design decision 10)
        [Test]
        public async Task StartAsync_WithPathAndNoQueueSettings_StartsInLocalMode()
        {
            string[] args = { "--path", AppContext.BaseDirectory, "--Scanner:Database:ConnectionString", "Server=unused;Database=unused" };

            using (IHost host = Program.CreateBuilder(args).Build())
            {
                await host.StartAsync();

                using (IServiceScope scope = host.Services.CreateScope())
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(scope.ServiceProvider.GetService<LocalScanRunner>(), Is.Not.Null);
                    Assert.That(host.Services.GetService<ScanOrchestrator>(), Is.Null);
                }

                await host.StopAsync();
            }
        }
```

- [ ] **Step 7: Run them to make sure they fail**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~LocalModeTests|FullyQualifiedName~ProgramTests" -p:CollectCoverage=false`
Expected: the end-to-end tests fail. Without `--path` handling, `Program` tries queue mode and exits 2 on the missing queue name. `CreateBuilder_RegistersTheSecretScannerWithAllSevenRules` fails because nothing registers `ISecretRule`.

- [ ] **Step 8: Wire local mode into Program**

Replace `src/RepoScanner.Scanner/Program.cs` with:
```csharp
using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RepoScanner.Core.Persistence;
using RepoScanner.Core.Scanning;
using RepoScanner.Data;
using RepoScanner.Data.Repositories;
using RepoScanner.Scanner.Configuration;
using RepoScanner.Scanner.Files;
using RepoScanner.Scanner.Local;
using RepoScanner.Scanner.Secrets;
using RepoScanner.Scanner.Secrets.Rules;
using Serilog;
using Serilog.Events;

namespace RepoScanner.Scanner
{
    public static class Program
    {
        // Time, level, class name, message
        private const string ConsoleOutputTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

        // The return value is the process exit code the Container Apps Job sees (see ExitCodes).
        public static async Task<int> Main(string[] args)
        {
            return await RunAsync(args, configureServices: null);
        }

        // Separate from Main so tests can add services (e.g. one that fails on purpose) and check
        // the exit code. Internal: visible to RepoScanner.Scanner.Tests only (see the .csproj).
        internal static async Task<int> RunAsync(string[] args, Action<IServiceCollection>? configureServices)
        {
            // Start-up logger: exists before the host is built and after it is disposed, so a
            // failure to start is always logged. The host has its own logger for normal running.
            Log.Logger = new LoggerConfiguration()
                .Enrich.WithProperty("SourceContext", typeof(Program).FullName)
                .WriteTo.Console(outputTemplate: ConsoleOutputTemplate)
                .CreateLogger();

            try
            {
                // --path <folder> scans a folder on this machine instead of reading the queue (#20). A
                // missing or wrong folder is a settings problem: one line and exit code 2, like the others.
                string? localFolder = null;
                if (LocalPathArgument.TryGet(args, out string? givenFolder))
                {
                    if (givenFolder == null || !Directory.Exists(givenFolder))
                    {
                        Log.Fatal("{Argument} must name a folder that exists, scanner not started: {Folder}",
                            LocalPathArgument.Name, givenFolder ?? "(no folder given)");
                        return ExitCodes.InvalidConfiguration;
                    }

                    localFolder = Path.GetFullPath(givenFolder);
                }

                HostApplicationBuilder builder = CreateBuilder(args);
                configureServices?.Invoke(builder.Services);

                using (IHost host = builder.Build())
                {
                    // Starting the host runs start-up checks, including settings validation
                    await host.StartAsync();

                    // Stops the work cleanly if the job is asked to shut down (Ctrl+C locally, SIGTERM in Azure)
                    IHostApplicationLifetime lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

                    if (localFolder != null)
                    {
                        // LocalScanRunner uses the scoped DbContext and repositories, so it runs in a scope of its own
                        using (IServiceScope scope = host.Services.CreateScope())
                        {
                            LocalScanRunner runner = scope.ServiceProvider.GetRequiredService<LocalScanRunner>();
                            await runner.RunAsync(localFolder, lifetime.ApplicationStopping);
                        }
                    }
                    else
                    {
                        ScanOrchestrator orchestrator = host.Services.GetRequiredService<ScanOrchestrator>();
                        await orchestrator.RunAsync(lifetime.ApplicationStopping);
                    }

                    await host.StopAsync();
                }

                return ExitCodes.Success;
            }
            catch (OptionsValidationException exception)
            {
                // A settings problem is fixed by changing settings, not code: list what is wrong, no stack trace
                Log.Fatal("Invalid configuration, scanner not started: {ValidationFailures}", exception.Failures);
                return ExitCodes.InvalidConfiguration;
            }
            catch (Exception exception)
            {
                // Anything else is unexpected: log it with the full exception so it can be diagnosed
                Log.Fatal(exception, "Scanner failed");
                return ExitCodes.Failure;
            }
            finally
            {
                // Write out anything still buffered before the process exits
                await Log.CloseAndFlushAsync();
            }
        }

        // Configures the host without running it, so tests can build it and inspect the services.
        public static HostApplicationBuilder CreateBuilder(string[] args)
        {
            // Sets up configuration (appsettings.json, user-secrets in Development,
            // environment variables, command line), dependency injection and default logging.
            // Content root is the app's own folder, so appsettings files are found wherever
            // the app is started from (dotnet run from the repo root, or /app in the container).
            HostApplicationBuilderSettings settings = new HostApplicationBuilderSettings
            {
                Args = args,
                ContentRootPath = AppContext.BaseDirectory
            };
            HostApplicationBuilder builder = Host.CreateApplicationBuilder(settings);

            // Remove the default logging providers (Console, Debug, EventSource, EventLog):
            // Serilog is the only place logs go
            builder.Logging.ClearProviders();

            builder.Services.AddSerilog(logger => logger
                // Our own code logs from Information upwards
                .MinimumLevel.Information()
                // Framework messages (e.g. "Application started. Press Ctrl+C") only when they matter
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                // The host's own "Hosting failed to start" error is not needed: Main logs every
                // start-up failure once, with the matching exit code
                .MinimumLevel.Override("Microsoft.Extensions.Hosting", LogEventLevel.Fatal)
                // Lets later code add properties (e.g. CorrelationId, Issue 10) to every log line in a scope
                .Enrich.FromLogContext()
                .WriteTo.Console(outputTemplate: ConsoleOutputTemplate),
                // Keep the start-up logger in Log.Logger: the host's logger is disposed with the host,
                // before Main's catch blocks run
                preserveStaticLogger: true);

            // Database settings: read from the Scanner:Database section and checked when the host starts,
            // like the queue settings. Required in every mode: the scanner's job is to save findings.
            builder.Services.AddOptions<DatabaseOptions>()
                .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            // The shared EF Core context and repositories from RepoScanner.Data, one of each per scope.
            // The connection string comes from the validated settings, and a transient SQL error (such as
            // Azure SQL failing over) is retried rather than failing the scan (ADR-015). The repositories
            // use one SaveChanges per method and no explicit transactions, which retrying supports.
            builder.Services.AddDbContext<ScannerDbContext>((services, options) =>
            {
                DatabaseOptions database = services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
                options.UseSqlServer(database.ConnectionString, sql => sql.EnableRetryOnFailure());
            });
            builder.Services.AddScoped<IScanRunRepository, ScanRunRepository>();
            builder.Services.AddScoped<IFindingRepository, FindingRepository>();

            // The secret scanner (#16, #17). Singletons: the walker and the rules keep nothing between scans.
            builder.Services.AddSingleton<RepositoryFileWalker>();
            builder.Services.AddSingleton<ISecretRule, Sec001PrivateKeyRule>();
            builder.Services.AddSingleton<ISecretRule, Sec002AzureSharedKeyRule>();
            builder.Services.AddSingleton<ISecretRule, Sec003ConnectionStringPasswordRule>();
            builder.Services.AddSingleton<ISecretRule, Sec004UrlCredentialsRule>();
            builder.Services.AddSingleton<ISecretRule, Sec005EntraClientSecretRule>();
            builder.Services.AddSingleton<ISecretRule, Sec006NuGetApiKeyRule>();
            builder.Services.AddSingleton<ISecretRule, Sec007NamedSettingRule>();
            builder.Services.AddSingleton<IScanner, SecretScanner>();

            if (LocalPathArgument.TryGet(args, out _))
            {
                // Local mode reads no queue, so queue settings are neither registered nor required (#20).
                // Scoped, because it uses the scoped repositories.
                builder.Services.AddScoped<LocalScanRunner>();
            }
            else
            {
                // Queue settings: read from the Scanner:Queue section, checked against the rules on
                // QueueOptions, and checked when the host starts rather than when first used
                builder.Services.AddOptions<QueueOptions>()
                    .Bind(builder.Configuration.GetSection(QueueOptions.SectionName))
                    .ValidateDataAnnotations()
                    .ValidateOnStart();

                builder.Services.AddSingleton<ScanOrchestrator>();
            }

            return builder;
        }
    }
}
```

- [ ] **Step 9: Run them to make sure they pass**

Run: `dotnet test tests/RepoScanner.Scanner.Tests --filter "FullyQualifiedName~LocalModeTests|FullyQualifiedName~ProgramTests|FullyQualifiedName~DatabaseRegistrationTests" -p:CollectCoverage=false`
Expected: `Passed: 18`.

- [ ] **Step 10: Document local mode in the README**

In `README.md`, add this section after the "Database and migrations" section and before "Exit codes":
````markdown
## Scan a local folder

The scanner can scan a folder on your machine instead of reading the queue. It runs the secret scanner and saves the findings to the database in `Scanner:Database:ConnectionString`:

```bash
dotnet run --project src/RepoScanner.Scanner -- --path tests/fixtures/secrets-repo
```

- The folder is saved in `Repositories` with the project name `Local`, and each scan as a row in `ScanRuns`.
- Each finding has a masked preview and a SHA-256 hash of the secret. The secret itself is never saved or logged ([ADR-023](docs/adr/ADR-023-secret-hash-is-an-unsalted-sha-256.md)).
- Running the same folder again adds only findings that are not already stored. Resolving and reopening findings comes with Issue 4.
- Folders named `.git`, `bin`, `obj`, `node_modules`, `packages`, `.vs` and `TestResults`, files over 1 MB, binary files, symbolic links and files that cannot be opened are skipped.
- So is any folder below the scanned one that contains a `.reposcanner-exclude` file. RepoScanner's own test data has one, so its fake secrets are not reported when this repository is scanned.
- `tests/fixtures/secrets-repo` is a pretend repository whose secrets are all fake, for trying the scanner out.
````

In the exit codes table, replace the row for `2` with:
```markdown
| `2` | Invalid configuration, or a `--path` that does not name an existing folder: the scanner did not start (logged as one line saying what is wrong) |
```

In the repository layout block, after the `tests/` line's three test projects, add:
```text
  fixtures/                   pretend repositories for the scanners' tests (every secret in them is fake)
```

- [ ] **Step 11: Run everything**

Run: `dotnet test RepoScanner.sln`
Expected: every project `Passed!`, Core and Scanner coverage at 80% or more.

- [ ] **Step 12: Check it against the local SQL Server**

In a new terminal (so it sees the `Scanner__Database__ConnectionString` user variable), run:
```bash
dotnet run --project src/RepoScanner.Scanner -- --path tests/fixtures/secrets-repo
```
Expected: exit code 0, nine `New finding` lines, and a summary ending `9 found, 9 new, 0 already stored`. Run the same command again. Expected: `9 found, 0 new, 9 already stored`.

In SSMS, on `.\SQLEXPRESS`:
```sql
SELECT r.ProjectName, r.Name, f.RuleId, f.FilePath, f.LineNumber, f.MaskedPreview
FROM Findings f JOIN Repositories r ON r.Id = f.RepositoryId
ORDER BY f.FilePath, f.LineNumber;

SELECT Id, Status, NewCount FROM ScanRuns ORDER BY Id;
```
Expected: nine findings under `Local` / `secrets-repo`, with previews such as `FAKE****` and `MIIE****`, and two `Completed` runs with `NewCount` 9 and 0.

- [ ] **Step 13: Stop for review, then commit**

Show the user the diff and the manual check's output, and wait for approval. Then:
```bash
git add src/RepoScanner.Scanner/Local/LocalPathArgument.cs src/RepoScanner.Scanner/Program.cs tests/RepoScanner.Scanner.Tests/Local tests/RepoScanner.Scanner.Tests/ProgramTests.cs README.md
git commit -m "This commit adds the --path local mode to the console app, with end-to-end tests and a README section #20"
```

---

### Task 12: Whole-branch check

**Files:** none changed.

- [ ] **Step 1: Build with zero warnings**

Run: `dotnet build RepoScanner.sln`
Expected: `Build succeeded` with `0 Warning(s)` and `0 Error(s)`.

- [ ] **Step 2: Run every test and read the coverage table**

Run: `dotnet test RepoScanner.sln --tl:off`
Expected: every project `Passed!`. Coverage: Core and Scanner at 80% or more; Data reported but not enforced.

- [ ] **Step 3: Check the files follow the house rules**

Run: `git diff develop --name-only`, then check that no changed file contains an em dash (U+2014), and run `git log develop..HEAD --format=%B` to check that every commit message starts `This commit`, ends with a task number and mentions no AI tool.

- [ ] **Step 4 (optional): Scan this repository itself**

Run: `dotnet run --project src/RepoScanner.Scanner -- --path .`
Expected: no finding under `tests/fixtures/` or `tests/RepoScanner.Scanner.Tests/Secrets/`, because both are marked. Any other finding is either a real problem or a false positive worth noting in the pull request. This adds a `Local` / `RepoScanner` row to the local database. This plan file contains the fixture's fake values, so it will be reported if it is still in `docs/superpowers/plans/`.

- [ ] **Step 5: Report**

Tell the user the results of Steps 1 to 4, and that the branch is ready for a pull request into `develop` when they want one.
