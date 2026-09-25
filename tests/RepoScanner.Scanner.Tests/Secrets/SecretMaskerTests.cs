using NUnit.Framework;
using RepoScanner.Scanner.Secrets;

namespace RepoScanner.Scanner.Tests.Secrets
{
    [TestFixture]
    public class SecretMaskerTests
    {
        // At most 4 characters, and never more than a quarter of the secret
        [TestCase("", "****")]
        [TestCase("abc", "****")]
        [TestCase("abcd", "a****")]
        [TestCase("Pa55word", "Pa****")]
        [TestCase("abcdefghijklmnop", "abcd****")]
        public void Mask_ShowsAtMostAQuarterOfTheSecret(string secret, string expected)
        {
            Assert.That(SecretMasker.Mask(secret), Is.EqualTo(expected));
        }

        // An 88-character storage key still shows only 4 characters
        [Test]
        public void Mask_ShowsNoMoreThanFourCharacters()
        {
            Assert.That(SecretMasker.Mask("FAKE" + new string('0', 84)), Is.EqualTo("FAKE****"));
        }

        // Always four stars, so the preview does not reveal how long the secret is
        [Test]
        public void Mask_DoesNotRevealTheLength()
        {
            Assert.That(SecretMasker.Mask(new string('a', 20)), Is.EqualTo(SecretMasker.Mask(new string('a', 80))));
        }
    }
}
