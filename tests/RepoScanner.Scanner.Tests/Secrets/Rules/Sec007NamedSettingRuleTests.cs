using NUnit.Framework;
using RepoScanner.Core.Enums;
using RepoScanner.Scanner.Secrets.Rules;

namespace RepoScanner.Scanner.Tests.Secrets.Rules
{
    [TestFixture]
    public class Sec007NamedSettingRuleTests
    {
        private readonly Sec007NamedSettingRule _rule = new Sec007NamedSettingRule();

        [Test]
        public void Rule_IsAMediumNamedSetting()
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_rule.RuleId, Is.EqualTo("SEC007"));
                Assert.That(_rule.Severity, Is.EqualTo(Severity.Medium));
                Assert.That(_rule.Message, Is.EqualTo("Password, secret or key in a named setting"));
            }
        }

        [TestCase("    \"ApiKey\": \"FAKE0payments0api0key0123456789\",", "FAKE0payments0api0key0123456789")]
        [TestCase("        private const string ApiKey = \"FAKE-csharp-key\";", "FAKE-csharp-key")]
        [TestCase("string password = @\"FAKE-verbatim\";", "FAKE-verbatim")]
        [TestCase("$apiKey = 'FAKE-powershell-key'", "FAKE-powershell-key")]
        [TestCase("  password: \"FAKE-yaml-quoted\"", "FAKE-yaml-quoted")]
        [TestCase("  password: FAKE-yaml-plain # a comment", "FAKE-yaml-plain")]
        [TestCase("    <add key=\"ClientSecret\" value=\"FAKE-client-secret-value-42\" />", "FAKE-client-secret-value-42")]
        [TestCase("DEVOPS_PAT=FAKEdevopspat000000000000000000000000000000000000000", "FAKEdevopspat000000000000000000000000000000000000000")]
        [TestCase("export DB_PASSWORD=FAKE-exported", "FAKE-exported")]
        [TestCase("\"AzureDevOpsPat\": \"FAKE-azure-devops-pat\"", "FAKE-azure-devops-pat")]
        [TestCase("pat: FAKE-plain-pat", "FAKE-plain-pat")]
        public void Pattern_FindsTheValue(string text, string expected)
        {
            Assert.That(RuleMatches.SecretsFoundBy(_rule, text), Is.EqualTo(new string[] { expected }));
        }

        // Names that only look secret, values that cannot be one, and ordinary lines of C#
        [TestCase("\"Path\": \"/var/FAKE-folder\"")]
        [TestCase("compat = \"FAKE-compat-value\"")]
        [TestCase("\"TokenEndpoint\": \"FAKE-token-endpoint\"")]
        [TestCase("\"SecretName\": \"FAKE-keyvault-secret-name\"")]
        [TestCase("\"Password\": \"short\"")]
        [TestCase("\"RefreshToken2\": \"https://login.example.com/token\"")]
        [TestCase("\"Password\": \"has spaces in it\"")]
        [TestCase("if (password == \"FAKE-compared-value\")")]
        [TestCase("            Password = options.Password;")]
        [TestCase("            ClientSecret = configuration[\"ClientSecret\"];")]
        [TestCase("    public Task RunAsync(CancellationToken cancellationToken)")]
        // Review fix: a hash, a pattern and a header name are not the secret itself
        [TestCase("\"SecretHash\": \"5e884898da28047151d0e56f8dc6292773603d0d6aabbdd62a11ef721d1542d8\"")]
        [TestCase("private const string PasswordPattern = @\"(?:Password|Pwd)=(?<secret>[^;]+)\";")]
        [TestCase("\"PasswordRegex\": \"^(?=.*[A-Z]).{8,}$\"")]
        [TestCase("ApiKeyHeader = \"X-Api-Key-Name\"")]
        public void Pattern_IgnoresLookAlikes(string text)
        {
            Assert.That(RuleMatches.SecretsFoundBy(_rule, text), Is.Empty);
        }

        // The rule still matches a placeholder; the allow-list (#19) drops it in SecretScanner
        [Test]
        public void Pattern_StillMatchesAPlaceholder()
        {
            Assert.That(RuleMatches.SecretsFoundBy(_rule, "\"Password\": \"changeme\""), Is.EqualTo(new string[] { "changeme" }));
        }
    }
}
