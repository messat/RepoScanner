using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RepoScanner.Core.Enums;

namespace RepoScanner.Scanner.CodePatterns.Rules
{
    // A catch block with nothing in it, so an error disappears without a trace. Low (Issue 6 design, CP006):
    // it hides failures rather than being an attack in itself. CA1031 is about catching general exceptions,
    // not empty blocks, so the reference says "related to".
    public sealed class Cp006EmptyCatchRule : ICodePatternRule
    {
        private static readonly string Message = "This catch block is empty, so errors are hidden. Log or handle the exception, or add a "
            + "comment saying why it is ignored (related to " + RuleReference.ForAnalyser("CA1031") + ")";

        public string RuleId => "CP006";

        public Severity Severity => Severity.Low;

        public IReadOnlyList<CodePatternMatch> FindMatches(SyntaxNode root)
        {
            Walker walker = new Walker();
            walker.Visit(root);
            return walker.Matches;
        }

        // No statements, and no comment between the braces. A comment there records a deliberate choice, such as
        // "the file may already be gone" (design decision 14). Comments are trivia on the braces, not statements.
        private static bool IsEmpty(BlockSyntax block)
        {
            IEnumerable<SyntaxTrivia> inside = block.OpenBraceToken.TrailingTrivia.Concat(block.CloseBraceToken.LeadingTrivia);
            return block.Statements.Count == 0
                && !inside.Any(trivia => trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia));
        }

        // Visits every node of one file. The match is the whole catch, with its type and filter.
        private sealed class Walker : CSharpSyntaxWalker
        {
            public List<CodePatternMatch> Matches { get; } = new List<CodePatternMatch>();

            public override void VisitCatchClause(CatchClauseSyntax node)
            {
                if (IsEmpty(node.Block))
                {
                    Matches.Add(new CodePatternMatch(node, Message));
                }

                base.VisitCatchClause(node);
            }
        }
    }
}
