using System.Collections.Generic;
using NUnit.Framework;
using RepoScanner.Core.Enums;
using RepoScanner.Scanner.Secrets.Rules;

namespace RepoScanner.Scanner.Tests.Secrets.Rules
{
    [TestFixture]
    public class Sec001PrivateKeyRuleTests
    {
        // Two lines of a fake key. The rule reports them joined, with the line break removed.
        private const string FirstLine = "MIIEFAKEprivateKeyForRepoScannerTestsAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        private const string SecondLine = "FAKEsecondLineOfTheKeyBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

        private readonly Sec001PrivateKeyRule _rule = new Sec001PrivateKeyRule();

        [Test]
        public void Rule_IsACriticalPrivateKey()
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_rule.RuleId, Is.EqualTo("SEC001"));
                Assert.That(_rule.Severity, Is.EqualTo(Severity.Critical));
                Assert.That(_rule.Message, Is.EqualTo("Private key"));
            }
        }

        [TestCase("")]
        [TestCase("RSA ")]
        [TestCase("EC ")]
        [TestCase("DSA ")]
        [TestCase("OPENSSH ")]
        [TestCase("ENCRYPTED ")]
        public void Pattern_FindsEachKindOfPrivateKey(string type)
        {
            IReadOnlyList<string> secrets = RuleMatches.SecretsFoundBy(_rule, Pem(type, "\n"));

            Assert.That(secrets, Is.EqualTo(new string[] { FirstLine + SecondLine }));
        }

        // Whitespace is removed, so a key saved with Windows line endings is the same secret
        [Test]
        public void Pattern_WithWindowsLineEndings_FindsTheSameSecret()
        {
            IReadOnlyList<string> secrets = RuleMatches.SecretsFoundBy(_rule, Pem("RSA ", "\r\n"));

            Assert.That(secrets, Is.EqualTo(new string[] { FirstLine + SecondLine }));
        }

        // Review fix: a key held in a JSON string, as in a service account key file, is one line with \n
        // escapes. With the escapes removed it is the same secret, so it hashes the same as the .pem form.
        [Test]
        public void Pattern_FindsAKeyWrittenOnOneLineWithJsonEscapes()
        {
            string json = "{ \"private_key\": \"-----BEGIN PRIVATE KEY-----\\n" + FirstLine + "\\n" + SecondLine + "\\n-----END PRIVATE KEY-----\\n\" }";

            Assert.That(RuleMatches.SecretsFoundBy(_rule, json), Is.EqualTo(new string[] { FirstLine + SecondLine }));
        }

        // An older password-protected RSA key has header lines inside the block
        [Test]
        public void Pattern_FindsAKeyWithEncryptionHeaders()
        {
            string pem = "-----BEGIN RSA PRIVATE KEY-----\nProc-Type: 4,ENCRYPTED\nDEK-Info: AES-128-CBC,FAKE0123456789ABCDEF\n\n"
                + FirstLine + "\n-----END RSA PRIVATE KEY-----\n";

            Assert.That(RuleMatches.SecretsFoundBy(_rule, pem), Has.Count.EqualTo(1));
        }

        [Test]
        public void Pattern_IgnoresAPublicKey()
        {
            string pem = "-----BEGIN PUBLIC KEY-----\n" + FirstLine + "\n-----END PUBLIC KEY-----\n";

            Assert.That(RuleMatches.SecretsFoundBy(_rule, pem), Is.Empty);
        }

        [Test]
        public void Pattern_IgnoresABlockWhoseEndDoesNotMatchItsBeginning()
        {
            string pem = "-----BEGIN RSA PRIVATE KEY-----\n" + FirstLine + "\n-----END EC PRIVATE KEY-----\n";

            Assert.That(RuleMatches.SecretsFoundBy(_rule, pem), Is.Empty);
        }

        private static string Pem(string type, string newLine)
        {
            return "-----BEGIN " + type + "PRIVATE KEY-----" + newLine
                + FirstLine + newLine
                + SecondLine + newLine
                + "-----END " + type + "PRIVATE KEY-----" + newLine;
        }
    }
}
