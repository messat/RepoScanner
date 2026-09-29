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
            return In(rule, "class Repository { async void Run() { " + statements + " } }");
        }

        public static IReadOnlyList<string> In(ICodePatternRule rule, string code)
        {
            SyntaxNode root = CSharpSyntaxTree.ParseText(code).GetRoot();
            return rule.FindMatches(root).Select(match => match.Node.ToString()).ToList();
        }
    }
}
