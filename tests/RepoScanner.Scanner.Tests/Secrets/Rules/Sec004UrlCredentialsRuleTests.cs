using NUnit.Framework;
using RepoScanner.Core.Enums;
using RepoScanner.Scanner.Secrets.Rules;

namespace RepoScanner.Scanner.Tests.Secrets.Rules
{
    [TestFixture]
    public class Sec004UrlCredentialsRuleTests
    {
        private readonly Sec004UrlCredentialsRule _rule = new Sec004UrlCredentialsRule();

        [Test]
        public void Rule_IsHighCredentialsInAUrl()
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_rule.RuleId, Is.EqualTo("SEC004"));
                Assert.That(_rule.Severity, Is.EqualTo(Severity.High));
                Assert.That(_rule.Message, Is.EqualTo("Credentials in a URL"));
            }
        }

        [TestCase("$feed = \"https://build-agent:FAKE-feed-pa55@pkgs.example.com/nuget/v3/index.json\"", "FAKE-feed-pa55")]
        [TestCase("mongodb+srv://app:FAKEmongo@cluster0.example.net/orders", "FAKEmongo")]
        [TestCase("git clone http://ci:FAKE-git-pass@git.example.com/repo.git", "FAKE-git-pass")]
        public void Pattern_FindsThePassword(string text, string expected)
        {
            Assert.That(RuleMatches.SecretsFoundBy(_rule, text), Is.EqualTo(new string[] { expected }));
        }

        // ADR-016: the clone URL Azure DevOps offers has a user name only, which is not a secret
        [TestCase("https://blacklight@dev.azure.com/blacklight/Lumina/_git/RepoScanner")]
        [TestCase("http://localhost:5000/api")]
        [TestCase("https://example.com/path?user=a:b")]
        public void Pattern_IgnoresLookAlikes(string text)
        {
            Assert.That(RuleMatches.SecretsFoundBy(_rule, text), Is.Empty);
        }
    }
}
