using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RepoScanner.Core.Enums;

namespace RepoScanner.Scanner.CodePatterns.Rules
{
    // SQL built by joining strings, which can let a value change the query: SQL injection (CA2100). High
    // (Issue 6 design, CP001). Only the SQL written in the call itself is checked, not a variable built
    // earlier (design decision 12).
    public sealed class Cp001SqlFromStringsRule : ICodePatternRule
    {
        private static readonly string Message = "SQL built by joining strings can allow SQL injection. Pass values as parameters, "
            + "or use FromSql or ExecuteSql (" + RuleReference.ForAnalyser("CA2100") + ")";

        // EF Core's methods that run SQL exactly as given. Their FromSql, ExecuteSql and ...Interpolated forms
        // turn each hole in an interpolated string into a parameter, so they are left out.
        private static readonly HashSet<string> RawSqlMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "FromSqlRaw", "ExecuteSqlRaw", "ExecuteSqlRawAsync", "SqlQueryRaw"
        };

        public string RuleId => "CP001";

        public Severity Severity => Severity.High;

        public IReadOnlyList<CodePatternMatch> FindMatches(SyntaxNode root)
        {
            Walker walker = new Walker();
            walker.Visit(root);
            return walker.Matches;
        }

        // A + that joins anything other than literals, an interpolated string with at least one hole, or a
        // call to string.Format or string.Concat
        private static bool IsBuiltFromStrings(ExpressionSyntax? sql)
        {
            return WithoutParentheses(sql) switch
            {
                BinaryExpressionSyntax join when join.IsKind(SyntaxKind.AddExpression) => !JoinsOnlyLiterals(join),
                InterpolatedStringExpressionSyntax interpolated => interpolated.Contents.OfType<InterpolationSyntax>().Any(),
                InvocationExpressionSyntax call => IsStringFormatOrConcat(call),
                _ => false
            };
        }

        // A loop over the joined parts rather than a method calling itself, so a file that joins thousands of
        // strings in one expression cannot overflow the stack here
        private static bool JoinsOnlyLiterals(BinaryExpressionSyntax join)
        {
            IEnumerable<SyntaxNode> parts = join.DescendantNodesAndSelf(node => IsJoinOrBrackets(node));
            return parts.All(part => IsJoinOrBrackets(part) || part is LiteralExpressionSyntax);
        }

        private static bool IsJoinOrBrackets(SyntaxNode node)
        {
            return node.IsKind(SyntaxKind.AddExpression) || node is ParenthesizedExpressionSyntax;
        }

        // string.Format, String.Concat, System.String.Format and so on
        private static bool IsStringFormatOrConcat(InvocationExpressionSyntax call)
        {
            if (call.Expression is not MemberAccessExpressionSyntax member)
            {
                return false;
            }

            string method = member.Name.Identifier.ValueText;
            bool isString = member.Expression is PredefinedTypeSyntax predefined && predefined.Keyword.IsKind(SyntaxKind.StringKeyword)
                || SyntaxNames.LastIdentifier(member.Expression) == "String";
            return isString && (method == "Format" || method == "Concat");
        }

        private static ExpressionSyntax? WithoutParentheses(ExpressionSyntax? expression)
        {
            while (expression is ParenthesizedExpressionSyntax parenthesized)
            {
                expression = parenthesized.Expression;
            }

            return expression;
        }

        private static ExpressionSyntax? FirstArgument(ArgumentListSyntax? arguments)
        {
            return arguments != null && arguments.Arguments.Count > 0 ? arguments.Arguments[0].Expression : null;
        }

        // Visits every node of one file. The three places SQL is given: a raw SQL method's first argument,
        // new SqlCommand's first argument, and a value assigned to CommandText, including in an object
        // initialiser. The match is the whole call, creation or assignment.
        private sealed class Walker : CSharpSyntaxWalker
        {
            public List<CodePatternMatch> Matches { get; } = new List<CodePatternMatch>();

            public override void VisitInvocationExpression(InvocationExpressionSyntax node)
            {
                if (SyntaxNames.LastIdentifier(node.Expression) is string method && RawSqlMethods.Contains(method)
                    && IsBuiltFromStrings(FirstArgument(node.ArgumentList)))
                {
                    Matches.Add(new CodePatternMatch(node, Message));
                }

                base.VisitInvocationExpression(node);
            }

            public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
            {
                if (SyntaxNames.LastIdentifier(node.Type) == "SqlCommand" && IsBuiltFromStrings(FirstArgument(node.ArgumentList)))
                {
                    Matches.Add(new CodePatternMatch(node, Message));
                }

                base.VisitObjectCreationExpression(node);
            }

            public override void VisitAssignmentExpression(AssignmentExpressionSyntax node)
            {
                if (node.IsKind(SyntaxKind.SimpleAssignmentExpression) && SyntaxNames.LastIdentifier(node.Left) == "CommandText"
                    && IsBuiltFromStrings(node.Right))
                {
                    Matches.Add(new CodePatternMatch(node, Message));
                }

                base.VisitAssignmentExpression(node);
            }
        }
    }
}
