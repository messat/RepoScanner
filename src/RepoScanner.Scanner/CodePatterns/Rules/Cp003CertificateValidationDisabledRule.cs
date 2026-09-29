using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RepoScanner.Core.Enums;

namespace RepoScanner.Scanner.CodePatterns.Rules
{
    // A TLS certificate check that lets every certificate through, so anyone between the app and the server
    // can read or change the traffic (CA5359). High (Issue 6 design, CP003).
    public sealed class Cp003CertificateValidationDisabledRule : ICodePatternRule
    {
        private static readonly string Message = "Certificate validation is switched off, so traffic can be read or changed in transit. "
            + "Remove the callback, or check the certificate in it (" + RuleReference.ForAnalyser("CA5359") + ")";

        // The callbacks that decide whether a server's certificate is trusted: HttpClientHandler's,
        // ServicePointManager's in .NET Framework, and SslClientAuthenticationOptions'
        private static readonly HashSet<string> CertificateCallbacks = new HashSet<string>(StringComparer.Ordinal)
        {
            "ServerCertificateCustomValidationCallback", "ServerCertificateValidationCallback", "RemoteCertificateValidationCallback"
        };

        // .NET's own callback that accepts every certificate
        private const string AcceptAnyValidator = "DangerousAcceptAnyServerCertificateValidator";

        public string RuleId => "CP003";

        public Severity Severity => Severity.High;

        public IReadOnlyList<CodePatternMatch> FindMatches(SyntaxNode root)
        {
            Walker walker = new Walker();
            walker.Visit(root);
            return walker.Matches;
        }

        // A lambda or anonymous method that only returns true, as it is or wrapped in a delegate's constructor,
        // as in new RemoteCertificateValidationCallback(delegate { return true; }), which older .NET Framework
        // code writes
        private static bool AcceptsEveryCertificate(ExpressionSyntax callback)
        {
            if (callback is ObjectCreationExpressionSyntax creation && creation.ArgumentList?.Arguments.Count == 1)
            {
                return SyntaxNames.AlwaysReturnsTrue(creation.ArgumentList.Arguments[0].Expression);
            }

            return SyntaxNames.AlwaysReturnsTrue(callback);
        }

        // Visits every node of one file: each assignment, with = or +=, of such a callback, and each use of
        // DangerousAcceptAnyServerCertificateValidator
        private sealed class Walker : CSharpSyntaxWalker
        {
            public List<CodePatternMatch> Matches { get; } = new List<CodePatternMatch>();

            public override void VisitAssignmentExpression(AssignmentExpressionSyntax node)
            {
                bool setsOrAdds = node.IsKind(SyntaxKind.SimpleAssignmentExpression) || node.IsKind(SyntaxKind.AddAssignmentExpression);
                if (setsOrAdds && SyntaxNames.LastIdentifier(node.Left) is string callback && CertificateCallbacks.Contains(callback)
                    && AcceptsEveryCertificate(node.Right))
                {
                    Matches.Add(new CodePatternMatch(node, Message));
                }

                base.VisitAssignmentExpression(node);
            }

            public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
            {
                if (node.Name.Identifier.ValueText == AcceptAnyValidator)
                {
                    Matches.Add(new CodePatternMatch(node, Message));
                }

                base.VisitMemberAccessExpression(node);
            }
        }
    }
}
