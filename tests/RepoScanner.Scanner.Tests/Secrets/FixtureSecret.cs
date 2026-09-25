namespace RepoScanner.Scanner.Tests.Secrets
{
    // One fake secret in the fixture: the rule that must report it, where, and its value with
    // whitespace removed, as SecretScanner reads it
    internal sealed record FixtureSecret(string RuleId, string FilePath, int LineNumber, string Value);
}
