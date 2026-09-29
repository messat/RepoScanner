namespace RepoScanner.Scanner.CodePatterns
{
    // The Microsoft guidance a code-pattern rule mirrors, which ends each finding's message (Issue 6 design,
    // decisions 5 and 6), so a developer can look up why the code was flagged
    public static class RuleReference
    {
        // CP005's reference: no Microsoft analyser covers AllowAnyOrigin, so it cites the page instead of an ID
        public const string EnableCors = "Enable CORS: https://learn.microsoft.com/aspnet/core/security/cors";

        // For example "CA2100: https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca2100"
        public static string ForAnalyser(string analyserId)
        {
            return analyserId + ": https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/" + analyserId.ToLowerInvariant();
        }
    }
}
