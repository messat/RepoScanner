using RepoScanner.Scanner.CodePatterns;
using RepoScanner.Scanner.CodePatterns.Rules;

namespace RepoScanner.Scanner.Tests.CodePatterns
{
    // A new instance of each of the six rules, in ID order, for tests that need all of them
    internal static class AllCodePatternRules
    {
        public static ICodePatternRule[] Create()
        {
            return new ICodePatternRule[]
            {
                new Cp001SqlFromStringsRule(),
                new Cp002InsecureDeserializationRule(),
                new Cp003CertificateValidationDisabledRule(),
                new Cp004WeakHashingRule(),
                new Cp005AnyOriginRule(),
                new Cp006EmptyCatchRule()
            };
        }
    }
}
