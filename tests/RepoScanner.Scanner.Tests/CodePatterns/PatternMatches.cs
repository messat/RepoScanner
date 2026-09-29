using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RepoScanner.Scanner.CodePatterns;

namespace RepoScanner.Scanner.Tests.CodePatterns
{
    // Runs one code-pattern rule over a few lines of C# and gives the code each match points at
    internal static class PatternMatches
    {
        // The statements go inside an async method of a class, so await and local variables parse as they would
        // in real code
        public static IReadOnlyList<string> InMethod(ICodePatternRule rule, string statements)
        {
            return FoundInMethod(rule, statements).Select(match => match.Node.ToString()).ToList();
        }

        public static IReadOnlyList<string> In(ICodePatternRule rule, string code)
        {
            return FoundIn(rule, code).Select(match => match.Node.ToString()).ToList();
        }

        // The matches themselves, for a rule whose message depends on what it found
        public static IReadOnlyList<CodePatternMatch> FoundInMethod(ICodePatternRule rule, string statements)
        {
            return FoundIn(rule, "class Repository { async void Run() { " + statements + " } }");
        }

        public static IReadOnlyList<CodePatternMatch> FoundIn(ICodePatternRule rule, string code)
        {
            SyntaxNode root = CSharpSyntaxTree.ParseText(code).GetRoot();
            return rule.FindMatches(root);
        }
    }
}
