using NUnit.Framework;
using RepoScanner.Core.Enums;
using RepoScanner.Scanner.Secrets.Rules;

namespace RepoScanner.Scanner.Tests.Secrets.Rules
{
    [TestFixture]
    public class Sec003ConnectionStringPasswordRuleTests
    {
        private readonly Sec003ConnectionStringPasswordRule _rule = new Sec003ConnectionStringPasswordRule();

        [Test]
        public void Rule_IsAHighConnectionStringPassword()
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_rule.RuleId, Is.EqualTo("SEC003"));
                Assert.That(_rule.Severity, Is.EqualTo(Severity.High));
                Assert.That(_rule.Message, Is.EqualTo("Password in a connection string"));
            }
        }

        [TestCase("\"Server=tcp:orders-FAKE.database.windows.net,1433;Database=Orders;User ID=orders_app;Password=FAKE-Orders-Pa55word;Encrypt=True\"", "FAKE-Orders-Pa55word")]
        [TestCase("Pwd = FAKE-pwd-first ;Uid=app;Data Source=db01", "FAKE-pwd-first")]
        [TestCase("Host=db;Username=app;Password=FAKEpostgres", "FAKEpostgres")]
        [TestCase("Data Source=db01;Initial Catalog=Orders;password=FAKE-lower-case", "FAKE-lower-case")]
        public void Pattern_FindsThePassword(string text, string expected)
        {
            Assert.That(RuleMatches.SecretsFoundBy(_rule, text), Is.EqualTo(new string[] { expected }));
        }

        [TestCase("Server=(localdb)\\mssqllocaldb;Database=Orders;Trusted_Connection=True")]
        [TestCase("Password=FAKE-no-server;Timeout=30")]
        [TestCase("Server=db01;OldPassword=FAKE-old")]
        [TestCase("\"Server=db01\", \"Password=FAKE-other-string;\"")]
        [TestCase("Server=db01;Password=;")]
        public void Pattern_IgnoresLookAlikes(string text)
        {
            Assert.That(RuleMatches.SecretsFoundBy(_rule, text), Is.Empty);
        }
    }
}
