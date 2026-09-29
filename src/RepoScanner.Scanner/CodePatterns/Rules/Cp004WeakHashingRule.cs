using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RepoScanner.Core.Enums;

namespace RepoScanner.Scanner.CodePatterns.Rules
{
    // A hash or cipher algorithm that is broken (MD5, DES, RC2: CA5351) or weak (SHA1, TripleDES: CA5350).
    // Medium (Issue 6 design, CP004): often used only for a checksum, where it is harmless, so the message says
    // to suppress it with that reason.
    public sealed class Cp004WeakHashingRule : ICodePatternRule
    {
        private static readonly HashSet<string> HashMethods = new HashSet<string>(StringComparer.Ordinal) { "Create", "HashData", "HashDataAsync" };

        private static readonly HashSet<string> CreateOnly = new HashSet<string>(StringComparer.Ordinal) { "Create" };

        // Each algorithm, its analyser, and the static methods that give it: the hash algorithms can also hash
        // directly (MD5.HashData), the ciphers only create an instance
        private static readonly Dictionary<string, WeakAlgorithm> Algorithms = new Dictionary<string, WeakAlgorithm>(StringComparer.Ordinal)
        {
            { "MD5", new WeakAlgorithm("MD5", "CA5351", HashMethods) },
            { "DES", new WeakAlgorithm("DES", "CA5351", CreateOnly) },
            { "RC2", new WeakAlgorithm("RC2", "CA5351", CreateOnly) },
            { "SHA1", new WeakAlgorithm("SHA1", "CA5350", HashMethods) },
            { "TripleDES", new WeakAlgorithm("TripleDES", "CA5350", CreateOnly) }
        };

        // The older classes that implement them, created with new
        private static readonly Dictionary<string, WeakAlgorithm> Classes = new Dictionary<string, WeakAlgorithm>(StringComparer.Ordinal)
        {
            { "MD5CryptoServiceProvider", Algorithms["MD5"] },
            { "DESCryptoServiceProvider", Algorithms["DES"] },
            { "RC2CryptoServiceProvider", Algorithms["RC2"] },
            { "SHA1CryptoServiceProvider", Algorithms["SHA1"] },
            { "SHA1Managed", Algorithms["SHA1"] },
            { "TripleDESCryptoServiceProvider", Algorithms["TripleDES"] }
        };

        public string RuleId => "CP004";

        public Severity Severity => Severity.Medium;

        public IReadOnlyList<CodePatternMatch> FindMatches(SyntaxNode root)
        {
            Walker walker = new Walker();
            walker.Visit(root);
            return walker.Matches;
        }

        private sealed record WeakAlgorithm(string Name, string AnalyserId, HashSet<string> Methods)
        {
            public string Message => Name + " is a weak algorithm. Use SHA-256 or stronger, or AES for encryption. If it is only a "
                + "checksum, suppress this finding with that reason (" + RuleReference.ForAnalyser(AnalyserId) + ")";
        }

        // Visits every node of one file: calls such as MD5.Create() and SHA1.HashData(bytes), and new ... of the
        // older classes. The match is the call or the creation.
        private sealed class Walker : CSharpSyntaxWalker
        {
            public List<CodePatternMatch> Matches { get; } = new List<CodePatternMatch>();

            public override void VisitInvocationExpression(InvocationExpressionSyntax node)
            {
                if (node.Expression is MemberAccessExpressionSyntax member && member.IsKind(SyntaxKind.SimpleMemberAccessExpression)
                    && SyntaxNames.LastIdentifier(member.Expression) is string name && Algorithms.TryGetValue(name, out WeakAlgorithm? algorithm)
                    && algorithm.Methods.Contains(member.Name.Identifier.ValueText))
                {
                    Matches.Add(new CodePatternMatch(node, algorithm.Message));
                }

                base.VisitInvocationExpression(node);
            }

            public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
            {
                if (SyntaxNames.LastIdentifier(node.Type) is string type && Classes.TryGetValue(type, out WeakAlgorithm? algorithm))
                {
                    Matches.Add(new CodePatternMatch(node, algorithm.Message));
                }

                base.VisitObjectCreationExpression(node);
            }
        }
    }
}
