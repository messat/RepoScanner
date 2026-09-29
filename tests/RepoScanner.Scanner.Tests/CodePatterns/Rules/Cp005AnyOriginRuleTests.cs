using System.Linq;
using NUnit.Framework;
using RepoScanner.Core.Enums;
using RepoScanner.Scanner.CodePatterns;
using RepoScanner.Scanner.CodePatterns.Rules;

namespace RepoScanner.Scanner.Tests.CodePatterns.Rules
{
    [TestFixture]
    public class Cp005AnyOriginRuleTests
    {
        private readonly Cp005AnyOriginRule _rule = new Cp005AnyOriginRule();

        // No Microsoft analyser covers this, so the message cites the Enable CORS page (design decision 5)
        [Test]
        public void Rule_IsMediumAndCitesTheEnableCorsPage()
        {
            CodePatternMatch match = PatternMatches.FoundInMethod(_rule, "policy.AllowAnyOrigin();").Single();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_rule.RuleId, Is.EqualTo("CP005"));
                Assert.That(_rule.Severity, Is.EqualTo(Severity.Medium));
                Assert.That(match.Message, Does.StartWith("Allowing any origin lets any website call this API")
                    .And.EndsWith("(Enable CORS: https://learn.microsoft.com/aspnet/core/security/cors)"));
            }
        }

        // AllowAnyOrigin, and SetIsOriginAllowed with a check that lets every origin through, which does the same.
        // The match is the call, with what comes before it in the chain.
        [TestCase("policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();", "policy.AllowAnyOrigin()")]
        [TestCase("options.AddPolicy(\"Open\", policy => policy.AllowAnyOrigin());", "policy.AllowAnyOrigin()")]
        [TestCase("policy.WithMethods(\"GET\").AllowAnyOrigin();", "policy.WithMethods(\"GET\").AllowAnyOrigin()")]
        [TestCase("policy.SetIsOriginAllowed(origin => true);", "policy.SetIsOriginAllowed(origin => true)")]
        [TestCase("policy.SetIsOriginAllowed(_ => { return true; });", "policy.SetIsOriginAllowed(_ => { return true; })")]
        public void FindMatches_FlagsEveryOriginAllowed(string statement, string expected)
        {
            Assert.That(PatternMatches.InMethod(_rule, statement), Is.EqualTo(new string[] { expected }));
        }

        [TestCase("policy.WithOrigins(\"https://portal.example.com\").AllowAnyHeader().AllowAnyMethod();")]
        [TestCase("policy.SetIsOriginAllowed(origin => new Uri(origin).Host == \"portal.example.com\");")]
        [TestCase("policy.SetIsOriginAllowed(IsAllowedOrigin);")]
        [TestCase("policy.AllowCredentials();")]
        public void FindMatches_IgnoresListedOrCheckedOrigins(string statement)
        {
            Assert.That(PatternMatches.InMethod(_rule, statement), Is.Empty);
        }
    }
}
