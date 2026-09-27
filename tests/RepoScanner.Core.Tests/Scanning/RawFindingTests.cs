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
                "MaskedPreview", "SecretHash", "PackageName", "PackageVersion", "AdvisoryUrl", "SnippetHash"
            }));
        }

        // Each property is copied onto a Finding of the same name when it is saved, except SnippetHash, which
        // only identifies a code-pattern finding and is carried by its fingerprint (Issue 4 design, decision 15)
        [Test]
        public void RawFinding_EachPropertyMatchesAFindingColumnOfTheSameType()
        {
            PropertyInfo[] saved = typeof(RawFinding)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.Name != nameof(RawFinding.SnippetHash))
                .ToArray();

            foreach (PropertyInfo property in saved)
            {
                PropertyInfo? column = typeof(Finding).GetProperty(property.Name);

                Assert.That(column?.PropertyType, Is.EqualTo(property.PropertyType), property.Name);
            }
        }
    }
}
