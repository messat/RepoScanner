using Microsoft.CodeAnalysis;

namespace RepoScanner.Scanner.CodePatterns
{
    // One piece of code a rule flags, and the finding's message, which ends with the rule's reference
    // (RuleReference). The scanner takes the line number and the snippet hash from the node.
    public sealed record CodePatternMatch(SyntaxNode Node, string Message);
}
