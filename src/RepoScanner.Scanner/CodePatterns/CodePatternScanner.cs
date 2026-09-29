using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;
using RepoScanner.Core.Enums;
using RepoScanner.Core.Scanning;
using RepoScanner.Scanner.Files;

namespace RepoScanner.Scanner.CodePatterns
{
    // Finds insecure code patterns (CP001 to CP006) in the C# files under a folder. Each file is parsed once
    // and every rule walks the same tree (Issue 6 design, ADR-018).
    public sealed class CodePatternScanner : IScanner
    {
        // A file whose parentheses and angle brackets nest deeper than this is not parsed (NestingDepth). About
        // seven times below where Roslyn's parser overflows the stack, and far deeper than code written by hand.
        public const int MaxNestingDepth = 200;

        // The error Roslyn's parser gives when it stops on code nested too deeply for it, leaving an empty tree
        private const string ParserGaveUpErrorId = "CS8078";

        // The newest C# the parser knows, and no preprocessor symbols defined, so code under #if DEBUG is left
        // out as a plain build leaves it out (design decision 9)
        private static readonly CSharpParseOptions ParseOptions = new CSharpParseOptions(LanguageVersion.Latest);

        private readonly RepositoryFileWalker _walker;
        private readonly IReadOnlyList<ICodePatternRule> _rules;
        private readonly ILogger<CodePatternScanner> _logger;

        public CodePatternScanner(RepositoryFileWalker walker, IEnumerable<ICodePatternRule> rules, ILogger<CodePatternScanner> logger)
        {
            _walker = walker;
            _rules = rules.ToList();
            _logger = logger;
        }

        public ScannerType ScannerType => ScannerType.CodePattern;

        public async Task<IReadOnlyList<RawFinding>> ScanAsync(string rootPath, CancellationToken cancellationToken)
        {
            // The walker's skip rules apply (bin, obj, .reposcanner-exclude, files over 1 MB); of what is left,
            // only C# files (design decision 7)
            List<RepositoryFile> files = _walker.GetFiles(rootPath)
                .Where(file => file.RelativePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .ToList();
            List<RawFinding> findings = new List<RawFinding>();
            int unreadableCount = 0;
            int tooDeepCount = 0;

            foreach (RepositoryFile file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Null when the file cannot be read now (locked or deleted since the walk); the walker has
                // logged a warning naming it, and the rest of the folder is still scanned
                string? text = await _walker.TryReadTextAsync(file, cancellationToken);
                if (text == null)
                {
                    unreadableCount++;
                    continue;
                }

                List<RawFinding>? fileFindings = FindingsIn(file.RelativePath, text, cancellationToken);
                if (fileFindings == null)
                {
                    tooDeepCount++;
                    continue;
                }

                findings.AddRange(fileFindings);
            }

            List<RawFinding> sorted = findings
                .OrderBy(finding => finding.FilePath, StringComparer.Ordinal)
                .ThenBy(finding => finding.LineNumber)
                .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
                .ToList();

            _logger.LogInformation(
                "Code pattern scan found {FindingCount} findings ({FindingsByRule}) in {FileCount} .cs files, could not read " +
                "{UnreadableCount} files and skipped {TooDeepCount} files nested too deeply to scan",
                sorted.Count, CountsByRule(sorted), files.Count - unreadableCount - tooDeepCount, unreadableCount, tooDeepCount);

            return sorted;
        }

        // Every rule's findings in one file, or null when its code is nested too deeply to scan (design decision
        // 17), which happens in one of three ways: the file nests deeper than MaxNestingDepth, so it is not
        // parsed; the parser stops by itself with CS8078; or a walker throws InsufficientExecutionStackException,
        // and the whole file is skipped, including what an earlier rule found in it. Broken C# still parses, so
        // the rules run on whatever did.
        private List<RawFinding>? FindingsIn(string filePath, string text, CancellationToken cancellationToken)
        {
            if (NestingDepth.Deepest(text) > MaxNestingDepth)
            {
                LogTooDeep(filePath);
                return null;
            }

            try
            {
                SyntaxTree tree = CSharpSyntaxTree.ParseText(text, ParseOptions, filePath, cancellationToken: cancellationToken);
                SyntaxNode root = tree.GetRoot(cancellationToken);
                if (root.ContainsDiagnostics && tree.GetDiagnostics(cancellationToken).Any(diagnostic => diagnostic.Id == ParserGaveUpErrorId))
                {
                    LogTooDeep(filePath);
                    return null;
                }

                List<RawFinding> findings = new List<RawFinding>();

                foreach (ICodePatternRule rule in _rules)
                {
                    foreach (CodePatternMatch match in rule.FindMatches(root))
                    {
                        findings.Add(ToFinding(rule, filePath, match));
                    }
                }

                return findings;
            }
            catch (InsufficientExecutionStackException)
            {
                LogTooDeep(filePath);
                return null;
            }
        }

        private void LogTooDeep(string filePath)
        {
            _logger.LogWarning("Skipped {FilePath}: its code is nested too deeply to scan", filePath);
        }

        private static RawFinding ToFinding(ICodePatternRule rule, string filePath, CodePatternMatch match)
        {
            return new RawFinding
            {
                ScannerType = ScannerType.CodePattern,
                RuleId = rule.RuleId,
                Severity = rule.Severity,
                FilePath = filePath,

                // Counted from 1, from where the matched code starts, leaving out comments before it
                LineNumber = match.Node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                Message = match.Message,
                SnippetHash = SnippetHash.Compute(match.Node)
            };
        }

        // For example "CP001: 2, CP006: 1", or "none"
        private static string CountsByRule(IReadOnlyList<RawFinding> findings)
        {
            if (findings.Count == 0)
            {
                return "none";
            }

            return string.Join(", ", findings
                .GroupBy(finding => finding.RuleId, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => group.Key + ": " + group.Count()));
        }
    }
}
