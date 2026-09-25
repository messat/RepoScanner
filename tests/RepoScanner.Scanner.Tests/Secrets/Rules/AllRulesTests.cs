using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using RepoScanner.Scanner.Secrets;

namespace RepoScanner.Scanner.Tests.Secrets.Rules
{
    [TestFixture]
    public class AllRulesTests
    {
        [Test]
        public void Rules_AreSec001ToSec007()
        {
            IEnumerable<string> ids = AllSecretRules.Create().Select(rule => rule.RuleId);

            Assert.That(ids, Is.EqualTo(new string[] { "SEC001", "SEC002", "SEC003", "SEC004", "SEC005", "SEC006", "SEC007" }));
        }

        // SecretScanner reads the "secret" group, and relies on the timeout to move past a slow pattern
        [TestCaseSource(typeof(AllSecretRules), nameof(AllSecretRules.Create))]
        public void Pattern_HasASecretGroupAndTheOneSecondTimeout(ISecretRule rule)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(rule.Pattern.GetGroupNames(), Does.Contain(SecretValue.GroupName), rule.RuleId);
                Assert.That(rule.Pattern.MatchTimeout, Is.EqualTo(TimeSpan.FromSeconds(1)), rule.RuleId);
                Assert.That(rule.Pattern.Options.HasFlag(RegexOptions.CultureInvariant), Is.True, rule.RuleId);
            }
        }
    }
}
