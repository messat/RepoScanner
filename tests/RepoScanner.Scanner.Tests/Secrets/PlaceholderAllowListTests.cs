using NUnit.Framework;
using RepoScanner.Scanner.Secrets;

namespace RepoScanner.Scanner.Tests.Secrets
{
    [TestFixture]
    public class PlaceholderAllowListTests
    {
        [TestCase("changeme")]
        [TestCase("CHANGEME")]
        [TestCase("change-me")]
        [TestCase("password")]
        [TestCase("secret")]
        [TestCase("placeholder")]
        [TestCase("redacted")]
        [TestCase("todo")]
        [TestCase("none")]
        [TestCase("null")]
        [TestCase("example")]
        [TestCase("dummy")]
        [TestCase("  changeme  ")]
        public void IsPlaceholder_ForAPlaceholderWord_IsTrue(string value)
        {
            Assert.That(PlaceholderAllowList.IsPlaceholder(value), Is.True);
        }

        // A reference to a value kept somewhere else is not the value itself
        [TestCase("${DB_PASSWORD}")]
        [TestCase("$(Build.SecretValue)")]
        [TestCase("#{PaymentsWebhookSecret}#")]
        [TestCase("%API_KEY%")]
        [TestCase("{{ .Values.password }}")]
        [TestCase("__SmtpPassword__")]
        [TestCase("<your-password>")]
        public void IsPlaceholder_ForAReference_IsTrue(string value)
        {
            Assert.That(PlaceholderAllowList.IsPlaceholder(value), Is.True);
        }

        [TestCase("xxxxxxxx")]
        [TestCase("********")]
        [TestCase("00000000")]
        [TestCase("XXXXxxxx")]
        [TestCase("your-api-key-here")]
        [TestCase("YOUR_TOKEN")]
        public void IsPlaceholder_ForFiller_IsTrue(string value)
        {
            Assert.That(PlaceholderAllowList.IsPlaceholder(value), Is.True);
        }

        // Only whole values count, so a real secret cannot slip through by containing a placeholder word
        [TestCase("changeme123")]
        [TestCase("FAKEexampleKey1234")]
        [TestCase("Pa55word")]
        [TestCase("x")]
        [TestCase("yourpassword")]
        [TestCase("#{unclosed")]
        [TestCase("<>")]
        public void IsPlaceholder_ForAnythingElse_IsFalse(string value)
        {
            Assert.That(PlaceholderAllowList.IsPlaceholder(value), Is.False);
        }
    }
}
