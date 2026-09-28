using NUnit.Framework;
using RepoScanner.Core.Enums;
using RepoScanner.Scanner.Packages;

namespace RepoScanner.Scanner.Tests.Packages
{
    // nuget.org's deprecation reasons onto Severity (Issue 5 design, decision 26)
    [TestFixture]
    public class DeprecationSeverityTests
    {
        [TestCase(new[] { "CriticalBugs" }, Severity.High)]
        [TestCase(new[] { "Legacy" }, Severity.Low)]
        [TestCase(new[] { "Other" }, Severity.Low)]
        [TestCase(new[] { "Legacy", "CriticalBugs" }, Severity.High, Description = "several reasons: the highest")]
        [TestCase(new[] { "legacy" }, Severity.Low, Description = "compared ignoring case")]
        [TestCase(new[] { "SomethingNew" }, Severity.Low, Description = "a reason nuget.org does not document")]
        [TestCase(new string[0], Severity.Low, Description = "deprecated with no reason given")]
        public void ToSeverity_TheReasons_GiveTheHighestOfTheirSeverities(string[] reasons, Severity expected)
        {
            Assert.That(DeprecationSeverity.ToSeverity(reasons), Is.EqualTo(expected));
        }

        [TestCase("CriticalBugs", ExpectedResult = true)]
        [TestCase("Legacy", ExpectedResult = true)]
        [TestCase("Other", ExpectedResult = true)]
        [TestCase("criticalbugs", ExpectedResult = true)]
        [TestCase("SomethingNew", ExpectedResult = false)]
        public bool IsKnown_IsTrueOnlyForTheReasonsNuGetOrgDocuments(string reason)
        {
            return DeprecationSeverity.IsKnown(reason);
        }
    }
}
