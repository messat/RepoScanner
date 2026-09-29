using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RepoScanner.Scanner.CodePatterns
{
    // Identifies a code-pattern finding without its line number, so it keeps its fingerprint, and its
    // suppression, when code above it changes (ADR-024, Issue 6 amendment)
    public static class SnippetHash
    {
        // SHA-256 of "<type>|<member>|<tokens>" as UTF-8, as 64 lower-case hex characters
        public static string Compute(SyntaxNode node)
        {
            string identity = TypeName(node) + "|" + MemberName(node) + "|" + Tokens(node);
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
            return Convert.ToHexStringLower(hash);
        }

        // The types the code sits in, outermost first, such as Outer.Inner. Namespaces are left out. Empty in
        // top-level statements.
        private static string TypeName(SyntaxNode node)
        {
            IEnumerable<string> names = node.Ancestors()
                .OfType<BaseTypeDeclarationSyntax>()
                .Select(type => type.Identifier.ValueText)
                .Reverse();
            return string.Join(".", names);
        }

        // The member the code sits in. Accessors, lambdas and local functions are not members, so code in
        // them belongs to the member around them. Empty when the code is in no member, such as an attribute
        // on a type, or in a member of another kind, such as an operator.
        private static string MemberName(SyntaxNode node)
        {
            foreach (SyntaxNode ancestor in node.Ancestors())
            {
                switch (ancestor)
                {
                    case MethodDeclarationSyntax method:
                        return method.Identifier.ValueText;
                    case ConstructorDeclarationSyntax constructor:
                        return constructor.Identifier.ValueText;
                    case PropertyDeclarationSyntax property:
                        return property.Identifier.ValueText;
                    case IndexerDeclarationSyntax:
                        return "this";
                    case EventDeclarationSyntax eventDeclaration:
                        return eventDeclaration.Identifier.ValueText;
                    case BaseFieldDeclarationSyntax field:
                        return FieldName(field, node);
                    case BaseTypeDeclarationSyntax:
                        return string.Empty;
                }
            }

            return string.Empty;
        }

        // One declaration can hold several fields (Action _first = null, _save = ...), so the one whose
        // initialiser holds the code; the first when the code is elsewhere in the declaration
        private static string FieldName(BaseFieldDeclarationSyntax field, SyntaxNode node)
        {
            SeparatedSyntaxList<VariableDeclaratorSyntax> variables = field.Declaration.Variables;
            VariableDeclaratorSyntax variable = variables.FirstOrDefault(candidate => candidate.Span.Contains(node.Span)) ?? variables[0];
            return variable.Identifier.ValueText;
        }

        // The code's tokens joined by single spaces. Spaces, line breaks and comments are trivia, not tokens,
        // so they are left out. A string over several lines holds its line breaks, which are counted as \n,
        // so a Windows checkout (CRLF) gives the same hash as the Linux container. Tokens the parser made up
        // for broken code are empty and left out.
        private static string Tokens(SyntaxNode node)
        {
            IEnumerable<string> tokens = node.DescendantTokens()
                .Where(token => !token.IsMissing)
                .Select(token => token.Text.Replace("\r\n", "\n", StringComparison.Ordinal));
            return string.Join(" ", tokens);
        }
    }
}
