using NUnit.Framework;
using RepoScanner.Core.Enums;
using RepoScanner.Scanner.Secrets.Rules;

namespace RepoScanner.Scanner.Tests.Secrets.Rules
{
    [TestFixture]
    public class Sec005EntraClientSecretRuleTests
    {
        // 3 characters, a digit, Q~, then 34 characters: 40 in all
        private const string ClientSecret = "FAK8Q~FAKE.client_secret~for-RepoScanner";

        private readonly Sec005EntraClientSecretRule _rule = new Sec005EntraClientSecretRule();

        [Test]
        public void Rule_IsAHighEntraClientSecret()
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_rule.RuleId, Is.EqualTo("SEC005"));
                Assert.That(_rule.Severity, Is.EqualTo(Severity.High));
                Assert.That(_rule.Message, Is.EqualTo("Entra ID client secret"));
            }
        }

        [Test]
        public void Pattern_FindsAClientSecretInCode()
        {
            string text = "public const string GraphClientSecret = \"" + ClientSecret + "\";";

            Assert.That(RuleMatches.SecretsFoundBy(_rule, text), Is.EqualTo(new string[] { ClientSecret }));
        }

        // 31 to 34 characters after Q~, and no more: a longer token is something else
        [TestCase(30)]
        [TestCase(35)]
        public void Pattern_IgnoresTheWrongLength(int length)
        {
            Assert.That(RuleMatches.SecretsFoundBy(_rule, "abc8Q~" + new string('x', length)), Is.Empty);
        }

        [Test]
        public void Pattern_IgnoresATokenWithoutQTilde()
        {
            Assert.That(RuleMatches.SecretsFoundBy(_rule, "abc8Qx" + new string('x', 34)), Is.Empty);
        }

        [Test]
        public void Pattern_IgnoresALowerCaseQ()
        {
            Assert.That(RuleMatches.SecretsFoundBy(_rule, "abc8q~" + new string('x', 34)), Is.Empty);
        }
    }
}
