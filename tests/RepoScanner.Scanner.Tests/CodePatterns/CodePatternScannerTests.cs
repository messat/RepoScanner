using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using NUnit.Framework;
using RepoScanner.Core.Enums;
using RepoScanner.Core.Scanning;
using RepoScanner.Scanner.CodePatterns;
using RepoScanner.Scanner.Files;
using RepoScanner.Scanner.Reconciliation;

namespace RepoScanner.Scanner.Tests.CodePatterns
{
    // The scanner with stand-in rules, so it is tested apart from CP001 to CP006: FlagRule reports every
    // call to Flag(), and the others fail the way a file or a rule can
    [TestFixture]
    public class CodePatternScannerTests
    {
        private TempFolder _folder = null!;
        private FakeLogger<CodePatternScanner> _logger = null!;

        [SetUp]
        public void SetUp()
        {
            _folder = new TempFolder();
            _logger = new FakeLogger<CodePatternScanner>();
        }

        [TearDown]
        public void TearDown()
        {
            _folder.Dispose();
        }

        [Test]
        public void ScannerType_IsCodePattern()
        {
            Assert.That(CreateScanner(new FlagRule("CP001")).ScannerType, Is.EqualTo(ScannerType.CodePattern));
        }

        [Test]
        public async Task ScanAsync_ReportsTheRuleWhereTheMatchIsAndItsSnippetHash()
        {
            _folder.WriteText("src/Orders.cs", "class Orders\n{\n    void Save() { Flag(); }\n}\n");

            RawFinding finding = (await ScanAsync(new FlagRule("CP001"))).Single();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(finding.ScannerType, Is.EqualTo(ScannerType.CodePattern));
                Assert.That(finding.RuleId, Is.EqualTo("CP001"));
                Assert.That(finding.Severity, Is.EqualTo(Severity.Medium));
                Assert.That(finding.Message, Is.EqualTo(FlagRule.Message));
                Assert.That(finding.FilePath, Is.EqualTo("src/Orders.cs"));
                Assert.That(finding.LineNumber, Is.EqualTo(3));
                Assert.That(finding.SnippetHash, Is.EqualTo(Sha256Hex("Orders|Save|Flag ( )")));
            }
        }

        // The reconciler refuses a finding without the parts of its fingerprint (ADR-024)
        [Test]
        public async Task ScanAsync_GivesFindingsTheFingerprintCalculatorAccepts()
        {
            _folder.WriteText("Orders.cs", "class Orders { void Save() { Flag(); } void Load() { Flag(); } }");

            IReadOnlyList<RawFinding> findings = await ScanAsync(new FlagRule("CP001"));

            Assert.That(findings.Select(FingerprintCalculator.Calculate).Distinct().Count(), Is.EqualTo(2));
        }

        // Design decision 7: .cs files only; the Razor views and scripts beside them are not C# the parser reads
        [Test]
        public async Task ScanAsync_ReadsOnlyCSharpFilesIgnoringCase()
        {
            string code = "class Orders { void Save() { Flag(); } }";
            foreach (string path in new string[] { "a.cs", "b/B.CS", "c.txt", "d.csx", "e.cshtml", "f.razor", "g.cs.bak" })
            {
                _folder.WriteText(path, code);
            }

            IReadOnlyList<RawFinding> findings = await ScanAsync(new FlagRule("CP001"));

            Assert.That(findings.Select(finding => finding.FilePath), Is.EqualTo(new string[] { "a.cs", "b/B.CS" }));
        }

        // The parser always gives a tree, so a file mid-edit or in a newer C# is still scanned for what parsed
        [Test]
        public async Task ScanAsync_ScansCodeThatDoesNotCompile()
        {
            _folder.WriteText("Broken.cs", "class Orders { void Save() { int total = ; Flag(); ");

            IReadOnlyList<RawFinding> findings = await ScanAsync(new FlagRule("CP001"));

            Assert.That(findings, Has.Count.EqualTo(1));
        }

        // Design decision 9, a known limitation: files are parsed as a plain build with no symbols defined
        [Test]
        public async Task ScanAsync_LeavesOutCodeThatAPlainBuildLeavesOut()
        {
            _folder.WriteText("Debug.cs", "#if DEBUG\nclass A { void M() { Flag(); } }\n#endif\n#if !DEBUG\nclass B { void M() { Flag(); } }\n#endif\n");

            IReadOnlyList<RawFinding> findings = await ScanAsync(new FlagRule("CP001"));

            Assert.That(findings.Select(finding => finding.LineNumber), Is.EqualTo(new int?[] { 5 }));
        }

        // The rules are given out of order, so the order comes from the sort, not from the registration
        [Test]
        public async Task ScanAsync_ReturnsFindingsSortedByFileThenLineThenRule()
        {
            _folder.WriteText("b.cs", "class B { void M() { Flag(); } }\n");
            _folder.WriteText("a.cs", "class A { void M() { Flag(); } }\n\nclass C { void M() { Flag(); } }\n");

            IReadOnlyList<RawFinding> findings = await ScanAsync(new FlagRule("CP002"), new FlagRule("CP001"));

            Assert.That(findings.Select(finding => (finding.FilePath, finding.LineNumber, finding.RuleId)), Is.EqualTo(new (string, int?, string)[]
            {
                ("a.cs", 1, "CP001"), ("a.cs", 1, "CP002"), ("a.cs", 3, "CP001"), ("a.cs", 3, "CP002"), ("b.cs", 1, "CP001"), ("b.cs", 1, "CP002")
            }));
        }

        // Design decision 17: Roslyn throws InsufficientExecutionStackException rather than overflowing the
        // stack on code nested too deeply. That file is skipped, including what an earlier rule found in it.
        [Test]
        public async Task ScanAsync_WhenAFileIsTooDeepToWalk_SkipsItWithAWarningAndScansTheRest()
        {
            _folder.WriteText("deep.cs", "class Deep { void M() { Flag(); } }");
            _folder.WriteText("fine.cs", "class Fine { void M() { Flag(); } }");

            IReadOnlyList<RawFinding> findings = await ScanAsync(new FlagRule("CP001"), new TooDeepRule());

            FakeLogRecord warning = _logger.Collector.GetSnapshot().Single(record => record.Level == LogLevel.Warning);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(findings.Select(finding => finding.FilePath), Is.EqualTo(new string[] { "fine.cs" }));
                Assert.That(warning.Message, Does.Contain("deep.cs"));
                Assert.That(_logger.LatestRecord.GetStructuredStateValue("TooDeepCount"), Is.EqualTo("1"));
            }
        }

        // Roslyn's parser overflows the stack, which ends the whole process, on a few thousand nested parentheses
        // or generic angle brackets, so a file nested deeper than the limit is not parsed at all
        [Test]
        public async Task ScanAsync_WhenAFileNestsDeeperThanTheLimit_SkipsItWithAWarningAndScansTheRest()
        {
            _folder.WriteText("deep.cs", "class Deep { void M() { Flag(); int i = " + Nested(CodePatternScanner.MaxNestingDepth + 1) + "; } }");
            _folder.WriteText("fine.cs", "class Fine { void M() { Flag(); int i = " + Nested(CodePatternScanner.MaxNestingDepth) + "; } }");

            IReadOnlyList<RawFinding> findings = await ScanAsync(new FlagRule("CP001"));

            FakeLogRecord warning = _logger.Collector.GetSnapshot().Single(record => record.Level == LogLevel.Warning);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(findings.Select(finding => finding.FilePath), Is.EqualTo(new string[] { "fine.cs" }));
                Assert.That(warning.Message, Does.Contain("deep.cs"));
                Assert.That(_logger.LatestRecord.GetStructuredStateValue("TooDeepCount"), Is.EqualTo("1"));
            }
        }

        // The case the limit exists for: without it, this test ends the test run with a stack overflow
        [Test]
        public async Task ScanAsync_OnAHundredThousandNestedParentheses_DoesNotCrash()
        {
            _folder.WriteText("hostile.cs", "class Hostile { int i = " + Nested(100000) + "; }");

            IReadOnlyList<RawFinding> findings = await ScanAsync(new FlagRule("CP001"));

            Assert.That(findings, Is.Empty);
        }

        // On other deep code Roslyn's parser stops by itself and returns an empty tree with error CS8078, so the
        // rules would find nothing there without saying so. Where it stops depends on the thread's stack, which is
        // larger on Linux: 5,000 nested blocks parse there, so the test nests far deeper than either system allows
        // (Issue 12, the pipeline's first Linux run)
        [Test]
        public async Task ScanAsync_WhenTheParserGivesUp_SkipsTheFileWithAWarning()
        {
            string braces = string.Concat(Enumerable.Repeat("{ ", 100000)) + string.Concat(Enumerable.Repeat("} ", 100000));
            _folder.WriteText("blocks.cs", "class Blocks { void M() { Flag(); " + braces + "} }");

            IReadOnlyList<RawFinding> findings = await ScanAsync(new FlagRule("CP001"));

            FakeLogRecord warning = _logger.Collector.GetSnapshot().Single(record => record.Level == LogLevel.Warning);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(findings, Is.Empty);
                Assert.That(warning.Message, Does.Contain("blocks.cs"));
                Assert.That(_logger.LatestRecord.GetStructuredStateValue("TooDeepCount"), Is.EqualTo("1"));
            }
        }

        // Any other exception is a bug in a rule: the scanner fails, and the run carries on without it
        // (Issue 5 design, Failed scanners)
        [Test]
        public async Task ScanAsync_WhenARuleThrowsAnythingElse_Throws()
        {
            _folder.WriteText("Orders.cs", "class Orders { }");

            await Assert.ThatAsync(() => ScanAsync(new BrokenRule()), Throws.InstanceOf<InvalidOperationException>());
        }

        [Test]
        public async Task ScanAsync_LogsASummaryWithTheFindingsPerRule()
        {
            _folder.WriteText("a.cs", "class A { void M() { Flag(); Flag(); } }");
            _folder.WriteText("b.cs", "class B { void M() { Flag(); } }");
            _folder.WriteText("notes.txt", "Flag();");

            await ScanAsync(new FlagRule("CP002"), new FlagRule("CP001"));

            FakeLogRecord summary = _logger.LatestRecord;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(summary.Level, Is.EqualTo(LogLevel.Information));
                Assert.That(summary.GetStructuredStateValue("FindingCount"), Is.EqualTo("6"));
                Assert.That(summary.GetStructuredStateValue("FindingsByRule"), Is.EqualTo("CP001: 3, CP002: 3"));
                Assert.That(summary.GetStructuredStateValue("FileCount"), Is.EqualTo("2"));
                Assert.That(summary.GetStructuredStateValue("UnreadableCount"), Is.EqualTo("0"));
                Assert.That(summary.GetStructuredStateValue("TooDeepCount"), Is.EqualTo("0"));
            }
        }

        [Test]
        public async Task ScanAsync_WithNoFindings_LogsNoneForTheFindingsPerRule()
        {
            _folder.WriteText("a.cs", "class A { }");

            await ScanAsync(new FlagRule("CP001"));

            Assert.That(_logger.LatestRecord.GetStructuredStateValue("FindingsByRule"), Is.EqualTo("none"));
        }

        // A file that passes the walk but cannot be read in full (another program has locked part of it
        // after the first 8,000 bytes) is skipped, and the rest is still scanned. Only Windows makes other
        // readers respect such a lock.
        [Test]
        public async Task ScanAsync_WhenAFileCannotBeReadInFull_SkipsItAndScansTheRest()
        {
            if (OperatingSystem.IsWindows())
            {
                string lockedPath = _folder.WriteText("Big.cs", "class Big { void M() { Flag(); } }\n//" + new string('a', 20000));
                _folder.WriteText("Orders.cs", "class Orders { void M() { Flag(); } }");
                CodePatternScanner scanner = CreateScanner(new FlagRule("CP001"));

                IReadOnlyList<RawFinding> findings;
                using (FileStream holder = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    holder.Lock(10000, 1000);
                    findings = await scanner.ScanAsync(_folder.FullPath, CancellationToken.None);
                }

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(findings.Select(finding => finding.FilePath), Is.EqualTo(new string[] { "Orders.cs" }));
                    Assert.That(_logger.LatestRecord.GetStructuredStateValue("UnreadableCount"), Is.EqualTo("1"));
                }
            }
            else
            {
                Assert.Ignore("Only Windows makes other readers respect a lock on part of a file");
            }
        }

        [Test]
        public async Task ScanAsync_WhenCancelled_Throws()
        {
            _folder.WriteText("a.cs", "class A { }");
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();

                await Assert.ThatAsync(() => CreateScanner(new FlagRule("CP001")).ScanAsync(_folder.FullPath, cancellation.Token),
                    Throws.InstanceOf<OperationCanceledException>());
            }
        }

        private CodePatternScanner CreateScanner(params ICodePatternRule[] rules)
        {
            return new CodePatternScanner(new RepositoryFileWalker(NullLogger<RepositoryFileWalker>.Instance), rules, _logger);
        }

        private Task<IReadOnlyList<RawFinding>> ScanAsync(params ICodePatternRule[] rules)
        {
            return CreateScanner(rules).ScanAsync(_folder.FullPath, CancellationToken.None);
        }

        private static string Sha256Hex(string identity)
        {
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        }

        // 1 inside the given number of parentheses, such as ((1)) for 2
        private static string Nested(int depth)
        {
            return new string('(', depth) + "1" + new string(')', depth);
        }

        private static IEnumerable<InvocationExpressionSyntax> FlagCalls(SyntaxNode root)
        {
            return root.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(call => call.Expression is IdentifierNameSyntax name && name.Identifier.ValueText == "Flag");
        }

        // Reports every call to Flag()
        private sealed class FlagRule : ICodePatternRule
        {
            public const string Message = "Flag called (CA0000: https://learn.microsoft.com/)";

            public FlagRule(string ruleId)
            {
                RuleId = ruleId;
            }

            public string RuleId { get; }

            public Severity Severity => Severity.Medium;

            public IReadOnlyList<CodePatternMatch> FindMatches(SyntaxNode root)
            {
                return FlagCalls(root).Select(call => new CodePatternMatch(call, Message)).ToList();
            }
        }

        // Stands in for Roslyn running out of stack on a file whose code is nested too deeply: here, any file
        // with a class named Deep
        private sealed class TooDeepRule : ICodePatternRule
        {
            public string RuleId => "CP098";

            public Severity Severity => Severity.Low;

            public IReadOnlyList<CodePatternMatch> FindMatches(SyntaxNode root)
            {
                if (root.DescendantNodes().OfType<ClassDeclarationSyntax>().Any(type => type.Identifier.ValueText == "Deep"))
                {
                    throw new InsufficientExecutionStackException();
                }

                return Array.Empty<CodePatternMatch>();
            }
        }

        // A rule with a bug
        private sealed class BrokenRule : ICodePatternRule
        {
            public string RuleId => "CP099";

            public Severity Severity => Severity.Low;

            public IReadOnlyList<CodePatternMatch> FindMatches(SyntaxNode root)
            {
                throw new InvalidOperationException("A bug in the rule");
            }
        }
    }
}
