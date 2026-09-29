using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RepoScanner.Core.Scanning;
using RepoScanner.Scanner.CodePatterns;
using RepoScanner.Scanner.Files;

namespace RepoScanner.Scanner.Tests.CodePatterns
{
    // RepoScanner is one of the repositories it scans, so its own code and tests must not report an insecure
    // pattern. The fixture's planted patterns sit in a folder marked with .reposcanner-exclude, and the tests hold
    // their C# in strings, which the rules do not read, so anything found here is either a false positive in a
    // rule or a real insecure pattern committed by mistake.
    [TestFixture]
    public class CodePatternSelfScanTests
    {
        [TestCase("src")]
        [TestCase("tests")]
        public async Task ScanAsync_OnThisRepositorysOwnFolder_FindsNothing(string folder)
        {
            CodePatternScanner scanner = new CodePatternScanner(
                new RepositoryFileWalker(NullLogger<RepositoryFileWalker>.Instance), AllCodePatternRules.Create(), NullLogger<CodePatternScanner>.Instance);

            IReadOnlyList<RawFinding> findings = await scanner.ScanAsync(Path.Combine(RepositoryRoot.FullPath, folder), CancellationToken.None);

            Assert.That(findings.Select(finding => finding.RuleId + " " + finding.FilePath + ":" + finding.LineNumber), Is.Empty);
        }
    }
}
