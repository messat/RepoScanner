using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using RepoScanner.Core.Enums;

namespace RepoScanner.Scanner.CodePatterns
{
    // One insecure pattern, CP001 to CP006 (Issue 6 design, Rules). CodePatternScanner parses each file once
    // and gives every rule the same tree. A rule looks only at the syntax, matching names as written (ADR-018).
    public interface ICodePatternRule
    {
        // CP001 and so on
        string RuleId { get; }

        Severity Severity { get; }

        // Every match in one file, given the root of its syntax tree
        IReadOnlyList<CodePatternMatch> FindMatches(SyntaxNode root);
    }
}
