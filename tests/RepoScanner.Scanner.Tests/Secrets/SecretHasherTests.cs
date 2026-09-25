using NUnit.Framework;
using RepoScanner.Scanner.Secrets;

namespace RepoScanner.Scanner.Tests.Secrets
{
    [TestFixture]
    public class SecretHasherTests
    {
        // The published SHA-256 test value for "abc" (FIPS 180-2)
        [Test]
        public void Hash_IsTheSha256OfTheUtf8Bytes()
        {
            Assert.That(SecretHasher.Hash("abc"), Is.EqualTo("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"));
        }

        // Fits the SecretHash column, varchar(64)
        [Test]
        public void Hash_Is64LowerCaseHexCharacters()
        {
            Assert.That(SecretHasher.Hash("FAKE-secret"), Does.Match("^[0-9a-f]{64}$"));
        }

        // ADR-023: unsalted, so the same secret always gives the same hash
        [Test]
        public void Hash_OfTheSameSecret_IsTheSame()
        {
            string first = SecretHasher.Hash("FAKE-secret");
            string second = SecretHasher.Hash("FAKE-secret");

            Assert.That(second, Is.EqualTo(first));
        }

        [Test]
        public void Hash_OfDifferentSecrets_IsDifferent()
        {
            Assert.That(SecretHasher.Hash("FAKE-secret-1"), Is.Not.EqualTo(SecretHasher.Hash("FAKE-secret-2")));
        }
    }
}
