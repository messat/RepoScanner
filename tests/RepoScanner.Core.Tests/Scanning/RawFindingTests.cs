using System.Linq;
using System.Reflection;
using NUnit.Framework;
using RepoScanner.Core.Entities;
using RepoScanner.Core.Scanning;

namespace RepoScanner.Core.Tests.Scanning
{
    [TestFixture]
    public class RawFindingTests
    {
        // Issue 3 design: a raw finding carries what a scanner knows and nothing else. In particular
        // there is no property that could hold a secret's raw value, so one cannot be saved by mistake.
        [Test]
        public void RawFinding_HasOnlyTheFindingColumnsAScannerCanKnow()
        {
            string[] names = typeof(RawFinding)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => property.Name)
                .ToArray();

            Assert.That(names, Is.EquivalentTo(new string[]
            {
                "ScannerType", "RuleId", "Severity", "FilePath", "LineNumber", "Message",
                "MaskedPreview", "SecretHash", "PackageName", "PackageVersion", "AdvisoryUrl"
            }));
        }

        // Each property is copied onto a Finding of the same name when it is saved
        [Test]
        public void RawFinding_EachPropertyMatchesAFindingColumnOfTheSameType()
        {
            foreach (PropertyInfo property in typeof(RawFinding).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                PropertyInfo? column = typeof(Finding).GetProperty(property.Name);

                Assert.That(column?.PropertyType, Is.EqualTo(property.PropertyType), property.Name);
            }
        }
    }
}
