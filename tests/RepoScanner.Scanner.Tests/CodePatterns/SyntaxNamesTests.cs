using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NUnit.Framework;
using RepoScanner.Scanner.CodePatterns;

namespace RepoScanner.Scanner.Tests.CodePatterns
{
    [TestFixture]
    public class SyntaxNamesTests
    {
        // A rule names a type however the code writes it, as in new System.Security.Cryptography.MD5CryptoServiceProvider():
        // alone, namespace-qualified, generic, or behind an alias
        [TestCase("MD5", "MD5")]
        [TestCase("System.Security.Cryptography.MD5", "MD5")]
        [TestCase("global::System.Security.Cryptography.MD5", "MD5")]
        [TestCase("global::MD5", "MD5")]
        [TestCase("List<int>", "List")]
        public void LastIdentifier_OfATypeName_GivesTheLastNameWritten(string code, string expected)
        {
            Assert.That(SyntaxNames.LastIdentifier(SyntaxFactory.ParseTypeName(code)), Is.EqualTo(expected));
        }

        // The same text written as an expression, as in System.Security.Cryptography.MD5.Create(), is a
        // member access rather than a type name
        [TestCase("db.Users.FromSqlRaw", "FromSqlRaw")]
        [TestCase("System.Security.Cryptography.MD5", "MD5")]
        [TestCase("@class", "class")]
        public void LastIdentifier_OfAMemberAccess_GivesTheLastNameWritten(string code, string expected)
        {
            Assert.That(SyntaxNames.LastIdentifier(SyntaxFactory.ParseExpression(code)), Is.EqualTo(expected));
        }

        [TestCase("Create()")]
        [TestCase("\"MD5\"")]
        public void LastIdentifier_OfCodeThatIsNotAName_IsNull(string code)
        {
            Assert.That(SyntaxNames.LastIdentifier(SyntaxFactory.ParseExpression(code)), Is.Null);
        }

        [TestCase("origin => true")]
        [TestCase("(message, certificate, chain, errors) => true")]
        [TestCase("(sender, certificate, chain, errors) => { return true; }")]
        [TestCase("(a, b) => (true)")]
        [TestCase("delegate { return true; }")]
        [TestCase("delegate (object sender) { return true; }")]
        [TestCase("((origin) => true)")]
        public void AlwaysReturnsTrue_ForALambdaOrAnonymousMethodThatOnlyReturnsTrue_IsTrue(string code)
        {
            Assert.That(SyntaxNames.AlwaysReturnsTrue(SyntaxFactory.ParseExpression(code)), Is.True);
        }

        [TestCase("origin => false")]
        [TestCase("(message, certificate, chain, errors) => errors == SslPolicyErrors.None")]
        [TestCase("(a, b) => { Log(a); return true; }")]
        [TestCase("(a, b) => { }")]
        [TestCase("delegate { return false; }")]
        [TestCase("ValidateCertificate")]
        [TestCase("true")]
        public void AlwaysReturnsTrue_ForAnythingElse_IsFalse(string code)
        {
            Assert.That(SyntaxNames.AlwaysReturnsTrue(SyntaxFactory.ParseExpression(code)), Is.False);
        }

        // An assignment's value can be missing in code that does not compile
        [Test]
        public void AlwaysReturnsTrue_ForNoExpression_IsFalse()
        {
            ExpressionSyntax? missing = null;

            Assert.That(SyntaxNames.AlwaysReturnsTrue(missing), Is.False);
        }
    }
}
