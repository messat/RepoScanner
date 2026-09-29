using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RepoScanner.Core.Enums;

namespace RepoScanner.Scanner.CodePatterns.Rules
{
    // A deserializer that lets the data it reads choose which types are created, so untrusted data can run an
    // attacker's code: the formatters Microsoft marks insecure, and Newtonsoft.Json's TypeNameHandling other
    // than None. High (Issue 6 design, CP002). SoapFormatter is left out: it has no analyser ID (decision 13).
    public sealed class Cp002InsecureDeserializationRule : ICodePatternRule
    {
        private static readonly string TypeNameHandlingMessage = "TypeNameHandling other than None lets the JSON choose which type is "
            + "created, which can run an attacker's code. Use TypeNameHandling.None (" + RuleReference.ForAnalyser("CA2326") + ")";

        // Each formatter and the analyser that marks it insecure
        private static readonly Dictionary<string, string> InsecureFormatters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "BinaryFormatter", "CA2300" },
            { "LosFormatter", "CA2305" },
            { "NetDataContractSerializer", "CA2310" },
            { "ObjectStateFormatter", "CA2315" }
        };

        // Every TypeNameHandling value but None. Only these, so reading the setting, as in
        // settings.TypeNameHandling.ToString(), is not a match.
        private static readonly HashSet<string> UnsafeTypeNameHandling = new HashSet<string>(StringComparer.Ordinal)
        {
            "Objects", "Arrays", "All", "Auto"
        };

        public string RuleId => "CP002";

        public Severity Severity => Severity.High;

        public IReadOnlyList<CodePatternMatch> FindMatches(SyntaxNode root)
        {
            Walker walker = new Walker();
            walker.Visit(root);
            return walker.Matches;
        }

        private static string FormatterMessage(string formatter, string analyserId)
        {
            return formatter + " can run an attacker's code when it reads untrusted data. Use System.Text.Json instead ("
                + RuleReference.ForAnalyser(analyserId) + ")";
        }

        // Visits every node of one file: each new ... of an insecure formatter, and each TypeNameHandling value
        // other than None, in code or in an attribute
        private sealed class Walker : CSharpSyntaxWalker
        {
            public List<CodePatternMatch> Matches { get; } = new List<CodePatternMatch>();

            public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
            {
                if (SyntaxNames.LastIdentifier(node.Type) is string type && InsecureFormatters.TryGetValue(type, out string? analyserId))
                {
                    Matches.Add(new CodePatternMatch(node, FormatterMessage(type, analyserId)));
                }

                base.VisitObjectCreationExpression(node);
            }

            public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
            {
                if (node.IsKind(SyntaxKind.SimpleMemberAccessExpression)
                    && SyntaxNames.LastIdentifier(node.Expression) == "TypeNameHandling"
                    && UnsafeTypeNameHandling.Contains(node.Name.Identifier.ValueText))
                {
                    Matches.Add(new CodePatternMatch(node, TypeNameHandlingMessage));
                }

                base.VisitMemberAccessExpression(node);
            }
        }
    }
}
