using System.Linq;
using NUnit.Framework;
using RepoScanner.Core.Enums;
using RepoScanner.Scanner.CodePatterns;
using RepoScanner.Scanner.CodePatterns.Rules;

namespace RepoScanner.Scanner.Tests.CodePatterns.Rules
{
    [TestFixture]
    public class Cp004WeakHashingRuleTests
    {
        private const string LearnPages = "https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/";

        private readonly Cp004WeakHashingRule _rule = new Cp004WeakHashingRule();

        [Test]
        public void Rule_IsMedium()
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_rule.RuleId, Is.EqualTo("CP004"));
                Assert.That(_rule.Severity, Is.EqualTo(Severity.Medium));
            }
        }

        // CA5351 covers the broken algorithms (MD5, DES, RC2) and CA5350 the weak ones (SHA1, TripleDES). The
        // message names the algorithm, whichever class or method was used to get it.
        [TestCase("MD5 md5 = MD5.Create();", "MD5.Create()", "MD5", "CA5351")]
        [TestCase("byte[] hash = MD5.HashData(bytes);", "MD5.HashData(bytes)", "MD5", "CA5351")]
        [TestCase("byte[] hash = await MD5.HashDataAsync(stream);", "MD5.HashDataAsync(stream)", "MD5", "CA5351")]
        [TestCase("MD5 md5 = System.Security.Cryptography.MD5.Create();", "System.Security.Cryptography.MD5.Create()", "MD5", "CA5351")]
        [TestCase("DES des = DES.Create();", "DES.Create()", "DES", "CA5351")]
        [TestCase("RC2 rc2 = RC2.Create();", "RC2.Create()", "RC2", "CA5351")]
        [TestCase("MD5 md5 = new MD5CryptoServiceProvider();", "new MD5CryptoServiceProvider()", "MD5", "CA5351")]
        [TestCase("DES des = new DESCryptoServiceProvider();", "new DESCryptoServiceProvider()", "DES", "CA5351")]
        [TestCase("RC2 rc2 = new RC2CryptoServiceProvider();", "new RC2CryptoServiceProvider()", "RC2", "CA5351")]
        [TestCase("SHA1 sha1 = SHA1.Create();", "SHA1.Create()", "SHA1", "CA5350")]
        [TestCase("byte[] hash = SHA1.HashData(bytes);", "SHA1.HashData(bytes)", "SHA1", "CA5350")]
        [TestCase("byte[] hash = await SHA1.HashDataAsync(stream);", "SHA1.HashDataAsync(stream)", "SHA1", "CA5350")]
        [TestCase("TripleDES tripleDes = TripleDES.Create();", "TripleDES.Create()", "TripleDES", "CA5350")]
        [TestCase("SHA1 sha1 = new SHA1CryptoServiceProvider();", "new SHA1CryptoServiceProvider()", "SHA1", "CA5350")]
        [TestCase("SHA1 sha1 = new SHA1Managed();", "new SHA1Managed()", "SHA1", "CA5350")]
        [TestCase("TripleDES tripleDes = new TripleDESCryptoServiceProvider();", "new TripleDESCryptoServiceProvider()", "TripleDES", "CA5350")]
        public void FindMatches_FlagsAWeakAlgorithm(string statement, string expectedNode, string algorithm, string analyserId)
        {
            CodePatternMatch match = PatternMatches.FoundInMethod(_rule, statement).Single();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(match.Node.ToString(), Is.EqualTo(expectedNode));
                Assert.That(match.Message, Does.StartWith(algorithm + " is a weak algorithm")
                    .And.Contain("checksum")
                    .And.EndsWith("(" + analyserId + ": " + LearnPages + analyserId.ToLowerInvariant() + ")"));
            }
        }

        // Strong algorithms and HMACs, and names that only look like a weak algorithm
        [TestCase("SHA256 sha256 = SHA256.Create();")]
        [TestCase("byte[] hash = SHA512.HashData(bytes);")]
        [TestCase("SHA384 sha384 = SHA384.Create();")]
        [TestCase("Aes aes = Aes.Create();")]
        [TestCase("HMACSHA256 hmac = new HMACSHA256(key);")]
        [TestCase("HMACSHA1 hmac = new HMACSHA1(key);")]
        [TestCase("string text = MD5Helper.Create();")]
        [TestCase("string checksum = file.MD5;")]
        [TestCase("DES des = DES.Parse(text);")]
        public void FindMatches_IgnoresStrongAlgorithms(string statement)
        {
            Assert.That(PatternMatches.InMethod(_rule, statement), Is.Empty);
        }
    }
}
