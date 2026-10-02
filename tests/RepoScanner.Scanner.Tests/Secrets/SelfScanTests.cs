using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RepoScanner.Core.Scanning;
using RepoScanner.Scanner.Files;
using RepoScanner.Scanner.Secrets;

namespace RepoScanner.Scanner.Tests.Secrets
{
    // RepoScanner is one of the repositories it scans, so its own code, tests and documents must not
    // report anything. The fake secrets sit in folders marked with .reposcanner-exclude, so anything found
    // here is either a false positive in a rule or a real secret committed by mistake.
    [TestFixture]
    public class SelfScanTests
    {
        [TestCase("src")]
        [TestCase("tests")]
        [TestCase("docs")]
        public async Task ScanAsync_OnThisRepositorysOwnFolder_FindsNothing(string folder)
        {
            SecretScanner scanner = new SecretScanner(
                new RepositoryFileWalker(NullLogger<RepositoryFileWalker>.Instance), AllSecretRules.Create(), NullLogger<SecretScanner>.Instance);

            IReadOnlyList<RawFinding> findings = await scanner.ScanAsync(Path.Combine(RepositoryRoot.FullPath, folder), CancellationToken.None);

            // Rule, file and line only, so a failure never prints a secret
            Assert.That(findings.Select(finding => finding.RuleId + " " + finding.FilePath + ":" + finding.LineNumber), Is.Empty);
        }

        // .env.example is committed and sits beside the real .env, so someone filling in the wrong file would commit
        // real secrets. Copied into a folder of its own, so the real .env is never scanned or printed.
        [Test]
        public async Task ScanAsync_OnTheEnvExampleFile_FindsNothing()
        {
            using (TempFolder temp = new TempFolder())
            {
                File.Copy(Path.Combine(RepositoryRoot.FullPath, ".env.example"), Path.Combine(temp.FullPath, ".env.example"));
                SecretScanner scanner = new SecretScanner(
                    new RepositoryFileWalker(NullLogger<RepositoryFileWalker>.Instance), AllSecretRules.Create(), NullLogger<SecretScanner>.Instance);

                IReadOnlyList<RawFinding> findings = await scanner.ScanAsync(temp.FullPath, CancellationToken.None);

                // Rule, file and line only, so a failure never prints a secret
                Assert.That(findings.Select(finding => finding.RuleId + " " + finding.FilePath + ":" + finding.LineNumber), Is.Empty);
            }
        }
    }
}
