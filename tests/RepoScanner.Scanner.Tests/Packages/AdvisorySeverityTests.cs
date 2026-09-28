using NUnit.Framework;
using RepoScanner.Core.Enums;
using RepoScanner.Scanner.Packages;

namespace RepoScanner.Scanner.Tests.Packages
{
    // nuget.org's severity numbers onto Severity (Issue 5 design, decision 15)
    [TestFixture]
    public class AdvisorySeverityTests
    {
        [TestCase(0, Severity.Low)]
        [TestCase(1, Severity.Medium)]
        [TestCase(2, Severity.High)]
        [TestCase(3, Severity.Critical)]
        public void ToSeverity_ADocumentedValue_IsItsSeverity(int value, Severity expected)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(AdvisorySeverity.ToSeverity(value), Is.EqualTo(expected));
                Assert.That(AdvisorySeverity.IsKnown(value), Is.True);
            }
        }

        // Over-rating is safer than dropping a vulnerability; the scanner logs a warning for it (#46)
        [TestCase(-1)]
        [TestCase(4)]
        [TestCase(99)]
        public void ToSeverity_AnUnknownValue_IsHigh(int value)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(AdvisorySeverity.ToSeverity(value), Is.EqualTo(Severity.High));
                Assert.That(AdvisorySeverity.IsKnown(value), Is.False);
            }
        }
    }
}
