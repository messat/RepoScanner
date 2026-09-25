using NUnit.Framework;
using RepoScanner.Core.Enums;
using RepoScanner.Scanner.Secrets.Rules;

namespace RepoScanner.Scanner.Tests.Secrets.Rules
{
    [TestFixture]
    public class Sec002AzureSharedKeyRuleTests
    {
        // A storage account key is 88 base64 characters; a Service Bus key is 44
        private const string StorageKey = "FAKEstorageAccountKeyForRepoScannerTests0000000000000000000000000000000000000000000000==";
        private const string ServiceBusKey = "FAKEserviceBusKeyForRepoScannerTests0000000=";

        private readonly Sec002AzureSharedKeyRule _rule = new Sec002AzureSharedKeyRule();

        [Test]
        public void Rule_IsACriticalAzureSharedKey()
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_rule.RuleId, Is.EqualTo("SEC002"));
                Assert.That(_rule.Severity, Is.EqualTo(Severity.Critical));
                Assert.That(_rule.Message, Is.EqualTo("Azure Storage or Service Bus key"));
            }
        }

        [Test]
        public void Pattern_FindsTheKeyInAStorageConnectionString()
        {
            string text = "DefaultEndpointsProtocol=https;AccountName=fakeaccount;AccountKey=" + StorageKey + ";EndpointSuffix=core.windows.net";

            Assert.That(RuleMatches.SecretsFoundBy(_rule, text), Is.EqualTo(new string[] { StorageKey }));
        }

        [Test]
        public void Pattern_FindsTheKeyInAServiceBusConnectionString()
        {
            string text = "Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=" + ServiceBusKey;

            Assert.That(RuleMatches.SecretsFoundBy(_rule, text), Is.EqualTo(new string[] { ServiceBusKey }));
        }

        // Azure reads connection string keys in any case
        [Test]
        public void Pattern_IgnoresTheCaseOfTheKeyName()
        {
            Assert.That(RuleMatches.SecretsFoundBy(_rule, "accountkey=" + StorageKey), Is.EqualTo(new string[] { StorageKey }));
        }

        [TestCase("AccountKey=abc123;")]
        [TestCase("AccountKey=<your-storage-key>;")]
        [TestCase("SharedAccessKeyName=RootManageSharedAccessKey;")]
        public void Pattern_IgnoresLookAlikes(string text)
        {
            Assert.That(RuleMatches.SecretsFoundBy(_rule, text), Is.Empty);
        }
    }
}
