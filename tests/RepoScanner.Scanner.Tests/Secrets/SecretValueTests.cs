using System;
using System.Text.RegularExpressions;
using NUnit.Framework;
using RepoScanner.Scanner.Secrets;

namespace RepoScanner.Scanner.Tests.Secrets
{
    [TestFixture]
    public class SecretValueTests
    {
        // Line endings and spaces never change the secret, so they never change its hash
        [Test]
        public void FromMatch_ReturnsTheSecretGroupWithoutWhitespace()
        {
            Regex pattern = SecretRulePattern.Create(@"key=(?<secret>[a-z\s]+);");
            Match match = pattern.Match("key=ab c\r\nd ;");

            Assert.That(SecretValue.FromMatch(match), Is.EqualTo("abcd"));
        }

        [Test]
        public void Create_UsesTheOneSecondTimeoutAndCultureInvariantMatching()
        {
            Regex pattern = SecretRulePattern.Create("a", RegexOptions.IgnoreCase);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pattern.MatchTimeout, Is.EqualTo(TimeSpan.FromSeconds(1)));
                Assert.That(pattern.Options, Is.EqualTo(RegexOptions.CultureInvariant | RegexOptions.IgnoreCase));
            }
        }
    }
}
