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

        // Review fix: variable references common in C#, PowerShell and shell scripts
        [TestCase("{password}")]
        [TestCase("{2}")]
        [TestCase("$env:API_KEY")]
        [TestCase("$Env:DB_PASSWORD")]
        [TestCase("$GIT_TOKEN")]
        [TestCase("$SA_PASSWORD_FROM_ENV")]
        public void IsPlaceholder_ForAVariableReference_IsTrue(string value)
        {
            Assert.That(PlaceholderAllowList.IsPlaceholder(value), Is.True);
        }

        // Review fix: a value that only repeats the name of its setting, like Password=$password or an
        // enum constant, is not a secret
        [TestCase("$password", "Password")]
        [TestCase("acquireTokenSilent", "AcquireTokenSilent")]
        [TestCase("api-key", "ApiKey")]
        public void IsPlaceholder_WhenTheValueIsTheSettingsOwnName_IsTrue(string value, string settingName)
        {
            Assert.That(PlaceholderAllowList.IsPlaceholder(value, settingName), Is.True);
        }

        [TestCase("password123", "Password")]
        [TestCase("FAKE-secret-1234", null)]
        public void IsPlaceholder_WhenTheValueIsNotTheSettingsOwnName_IsFalse(string value, string? settingName)
        {
            Assert.That(PlaceholderAllowList.IsPlaceholder(value, settingName), Is.False);
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
        [TestCase("{}")]
        [TestCase("$uperSecret1")]
        public void IsPlaceholder_ForAnythingElse_IsFalse(string value)
        {
            Assert.That(PlaceholderAllowList.IsPlaceholder(value), Is.False);
        }
    }
}
