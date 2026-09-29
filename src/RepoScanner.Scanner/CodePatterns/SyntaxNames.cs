using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RepoScanner.Scanner.CodePatterns
{
    // Questions about the shape of code that more than one rule asks. The rules match names as written,
    // without knowing their types (ADR-018), so these look only at the syntax.
    public static class SyntaxNames
    {
        // The last name in a type name or member access, so System.Security.Cryptography.MD5, global::MD5 and
        // MD5 all give MD5, and db.Users.FromSqlRaw gives FromSqlRaw. Null for anything that is not a name.
        public static string? LastIdentifier(SyntaxNode? node)
        {
            return node switch
            {
                SimpleNameSyntax name => name.Identifier.ValueText,
                QualifiedNameSyntax qualified => LastIdentifier(qualified.Right),
                AliasQualifiedNameSyntax aliased => LastIdentifier(aliased.Name),
                MemberAccessExpressionSyntax memberAccess => LastIdentifier(memberAccess.Name),
                _ => null
            };
        }

        // True for a lambda or anonymous method that does nothing but return true, such as
        // (message, certificate, chain, errors) => true or delegate { return true; }: a check that lets
        // everything through (CP003, CP005). A method group is false, because its body cannot be seen here.
        public static bool AlwaysReturnsTrue(ExpressionSyntax? expression)
        {
            if (WithoutParentheses(expression) is not AnonymousFunctionExpressionSyntax function)
            {
                return false;
            }

            if (function.ExpressionBody != null)
            {
                return IsTrueLiteral(function.ExpressionBody);
            }

            return function.Block != null
                && function.Block.Statements.Count == 1
                && function.Block.Statements[0] is ReturnStatementSyntax returned
                && IsTrueLiteral(returned.Expression);
        }

        private static bool IsTrueLiteral(ExpressionSyntax? expression)
        {
            return WithoutParentheses(expression) is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.TrueLiteralExpression);
        }

        private static ExpressionSyntax? WithoutParentheses(ExpressionSyntax? expression)
        {
            while (expression is ParenthesizedExpressionSyntax parenthesized)
            {
                expression = parenthesized.Expression;
            }

            return expression;
        }
    }
}
