using NUnit.Framework;
using RepoScanner.Core.Enums;
using RepoScanner.Scanner.Secrets.Rules;

namespace RepoScanner.Scanner.Tests.Secrets.Rules
{
    [TestFixture]
    public class Sec006NuGetApiKeyRuleTests
    {
        // oy2 and 43 lower-case letters and digits
        private const string ApiKey = "oy2fakenugetapikeyforreposcannertests000000000";

        private readonly Sec006NuGetApiKeyRule _rule = new Sec006NuGetApiKeyRule();

        [Test]
        public void Rule_IsAHighNuGetApiKey()
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_rule.RuleId, Is.EqualTo("SEC006"));
                Assert.That(_rule.Severity, Is.EqualTo(Severity.High));
                Assert.That(_rule.Message, Is.EqualTo("NuGet API key"));
            }
        }

        [Test]
        public void Pattern_FindsAKeyOnTheCommandLine()
        {
            string text = "dotnet nuget push ./out/*.nupkg --source $feed --api-key " + ApiKey;

            Assert.That(RuleMatches.SecretsFoundBy(_rule, text), Is.EqualTo(new string[] { ApiKey }));
        }

        [TestCase(42)]
        [TestCase(44)]
        public void Pattern_IgnoresTheWrongLength(int length)
        {
            Assert.That(RuleMatches.SecretsFoundBy(_rule, "oy2" + new string('a', length)), Is.Empty);
        }

        [Test]
        public void Pattern_IgnoresUpperCaseLetters()
        {
            Assert.That(RuleMatches.SecretsFoundBy(_rule, "oy2" + new string('A', 43)), Is.Empty);
        }
    }
}
