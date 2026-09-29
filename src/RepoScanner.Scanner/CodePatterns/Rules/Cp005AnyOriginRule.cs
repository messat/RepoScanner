using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RepoScanner.Core.Enums;

namespace RepoScanner.Scanner.CodePatterns.Rules
{
    // A CORS policy that lets every website call the API from a user's browser. Medium (Issue 6 design,
    // CP005): a risk mainly when the API trusts the user's cookies or sign-in. No Microsoft analyser covers it,
    // so it cites the Enable CORS page (design decision 5).
    public sealed class Cp005AnyOriginRule : ICodePatternRule
    {
        private static readonly string Message = "Allowing any origin lets any website call this API from a user's browser. "
            + "List the allowed origins with WithOrigins (" + RuleReference.EnableCors + ")";

        public string RuleId => "CP005";

        public Severity Severity => Severity.Medium;

        public IReadOnlyList<CodePatternMatch> FindMatches(SyntaxNode root)
        {
            Walker walker = new Walker();
            walker.Visit(root);
            return walker.Matches;
        }

        // AllowAnyOrigin(), or SetIsOriginAllowed with a check that lets every origin through, as in
        // SetIsOriginAllowed(origin => true)
        private static bool AllowsAnyOrigin(InvocationExpressionSyntax call)
        {
            switch (SyntaxNames.LastIdentifier(call.Expression))
            {
                case "AllowAnyOrigin":
                    return true;
                case "SetIsOriginAllowed":
                    return call.ArgumentList.Arguments.Count == 1 && SyntaxNames.AlwaysReturnsTrue(call.ArgumentList.Arguments[0].Expression);
                default:
                    return false;
            }
        }

        // Visits every node of one file. The match is the call, with what comes before it in the chain.
        private sealed class Walker : CSharpSyntaxWalker
        {
            public List<CodePatternMatch> Matches { get; } = new List<CodePatternMatch>();

            public override void VisitInvocationExpression(InvocationExpressionSyntax node)
            {
                if (AllowsAnyOrigin(node))
                {
                    Matches.Add(new CodePatternMatch(node, Message));
                }

                base.VisitInvocationExpression(node);
            }
        }
    }
}
