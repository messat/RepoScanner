using NUnit.Framework;
using RepoScanner.Scanner.CodePatterns;

namespace RepoScanner.Scanner.Tests.CodePatterns
{
    [TestFixture]
    public class RuleReferenceTests
    {
        // Microsoft Learn's pages for the analysers have lower-case addresses
        [TestCase("CA2100", "CA2100: https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca2100")]
        [TestCase("CA5359", "CA5359: https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca5359")]
        public void ForAnalyser_GivesTheIdAndItsMicrosoftLearnPage(string analyserId, string expected)
        {
            Assert.That(RuleReference.ForAnalyser(analyserId), Is.EqualTo(expected));
        }
    }
}
