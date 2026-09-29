using NUnit.Framework;
using RepoScanner.Scanner.CodePatterns;

namespace RepoScanner.Scanner.Tests.CodePatterns
{
    [TestFixture]
    public class NestingDepthTests
    {
        [TestCase("", 0)]
        [TestCase("class C { int i = ((1)); }", 2)]
        [TestCase("Dictionary<string, List<int>> map;", 2)]
        [TestCase("Func<(int, List<string>)> pick;", 3)]
        public void Deepest_CountsParenthesesAndAngleBracketsTogether(string code, int expected)
        {
            Assert.That(NestingDepth.Deepest(code), Is.EqualTo(expected));
        }

        // Comparisons and shifts open an angle bracket that nothing closes, so the count starts again at each
        // statement and block; a generic type never spans one
        [TestCase("a = x < y; b = x << 2; c = x < y;", 2)]
        [TestCase("if (a < b) { } if (c < d) { } while (e < f) { }", 2)]
        public void Deepest_StartsAngleBracketsAgainAtEachStatementAndBlock(string code, int expected)
        {
            Assert.That(NestingDepth.Deepest(code), Is.EqualTo(expected));
        }

        // A stray closing bracket, such as a smiley in a comment, does not make the count negative
        [Test]
        public void Deepest_IgnoresClosingBracketsWithNothingOpen()
        {
            Assert.That(NestingDepth.Deepest("// done :) :) ))) \n int i = ((1));"), Is.EqualTo(2));
        }
    }
}
