using System.Linq;
using NUnit.Framework;
using RepoScanner.Core.Enums;
using RepoScanner.Scanner.CodePatterns;
using RepoScanner.Scanner.CodePatterns.Rules;

namespace RepoScanner.Scanner.Tests.CodePatterns.Rules
{
    [TestFixture]
    public class Cp003CertificateValidationDisabledRuleTests
    {
        private readonly Cp003CertificateValidationDisabledRule _rule = new Cp003CertificateValidationDisabledRule();

        [Test]
        public void Rule_IsHighAndCitesCA5359()
        {
            CodePatternMatch match = PatternMatches.FoundInMethod(_rule, "handler.ServerCertificateCustomValidationCallback = (m, c, ch, e) => true;").Single();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_rule.RuleId, Is.EqualTo("CP003"));
                Assert.That(_rule.Severity, Is.EqualTo(Severity.High));
                Assert.That(match.Message, Does.StartWith("Certificate validation is switched off")
                    .And.EndsWith("(CA5359: https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca5359)"));
            }
        }

        // Each certificate callback, set with = or += in code or in an object initialiser, to a lambda or
        // anonymous method that lets every certificate through, including one wrapped in new ...Callback(...)
        // as older .NET Framework code writes it. The match is the whole assignment.
        [TestCase("handler.ServerCertificateCustomValidationCallback = (message, certificate, chain, errors) => true;",
            "handler.ServerCertificateCustomValidationCallback = (message, certificate, chain, errors) => true")]
        [TestCase("HttpClientHandler handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };",
            "ServerCertificateCustomValidationCallback = (_, _, _, _) => true")]
        [TestCase("ServicePointManager.ServerCertificateValidationCallback += (sender, certificate, chain, errors) => true;",
            "ServicePointManager.ServerCertificateValidationCallback += (sender, certificate, chain, errors) => true")]
        [TestCase("ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };",
            "ServicePointManager.ServerCertificateValidationCallback = delegate { return true; }")]
        [TestCase("ServicePointManager.ServerCertificateValidationCallback = new RemoteCertificateValidationCallback(delegate { return true; });",
            "ServicePointManager.ServerCertificateValidationCallback = new RemoteCertificateValidationCallback(delegate { return true; })")]
        [TestCase("options.RemoteCertificateValidationCallback = (sender, certificate, chain, errors) => { return true; };",
            "options.RemoteCertificateValidationCallback = (sender, certificate, chain, errors) => { return true; }")]
        public void FindMatches_FlagsACallbackThatAcceptsEveryCertificate(string statement, string expected)
        {
            Assert.That(PatternMatches.InMethod(_rule, statement), Is.EqualTo(new string[] { expected }));
        }

        // .NET's own accept-everything callback, however it is written or used. Assigning it is one finding: the
        // value is not a lambda, so the assignment itself does not match.
        [TestCase("handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;",
            "HttpClientHandler.DangerousAcceptAnyServerCertificateValidator")]
        [TestCase("Func<HttpRequestMessage, X509Certificate2, X509Chain, SslPolicyErrors, bool> accept = System.Net.Http.HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;",
            "System.Net.Http.HttpClientHandler.DangerousAcceptAnyServerCertificateValidator")]
        public void FindMatches_FlagsDangerousAcceptAnyServerCertificateValidator(string statement, string expected)
        {
            Assert.That(PatternMatches.InMethod(_rule, statement), Is.EqualTo(new string[] { expected }));
        }

        // A callback that checks something, or one set to a named method whose body cannot be seen here
        [TestCase("handler.ServerCertificateCustomValidationCallback = (message, certificate, chain, errors) => errors == SslPolicyErrors.None;")]
        [TestCase("handler.ServerCertificateCustomValidationCallback = ValidateCertificate;")]
        [TestCase("ServicePointManager.ServerCertificateValidationCallback = new RemoteCertificateValidationCallback(ValidateCertificate);")]
        [TestCase("handler.ServerCertificateCustomValidationCallback = null;")]
        [TestCase("handler.CheckCertificateRevocationList = true;")]
        [TestCase("handler.ClientCertificateOptions = ClientCertificateOption.Manual;")]
        [TestCase("callbacks.OnCompleted = (result) => true;")]
        public void FindMatches_IgnoresCallbacksThatCheck(string statement)
        {
            Assert.That(PatternMatches.InMethod(_rule, statement), Is.Empty);
        }
    }
}
