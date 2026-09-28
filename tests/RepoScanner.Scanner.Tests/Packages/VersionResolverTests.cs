using System.Collections.Generic;
using System.Linq;
using NuGet.Versioning;
using NUnit.Framework;
using RepoScanner.Scanner.Packages;

namespace RepoScanner.Scanner.Tests.Packages
{
    // Issue 5 design, Versions and severity (#45): the version a declared package really is
    [TestFixture]
    public class VersionResolverTests
    {
        // What nuget.org might list for a package, pre-releases included, out of order as a list can be
        private static readonly IReadOnlyList<NuGetVersion> Available = new[]
        {
            "13.0.1", "12.0.1-beta1", "12.0.1", "12.0.2", "12.0.3-beta1", "12.0.3-beta2", "12.0.3",
            "4.0.0", "4.5.0", "4.5.1", "9.0.2.1", "9.0.2.60", "9.0.2.9"
        }.Select(NuGetVersion.Parse).ToList();

        [TestCase("13.0.3", ExpectedResult = true)]
        [TestCase("9.0.2.60", ExpectedResult = true)]
        [TestCase("1.0.0-beta1", ExpectedResult = true)]
        [TestCase("12.0.*", ExpectedResult = false)]
        [TestCase("[4.0.0, 4.5.0]", ExpectedResult = false)]
        [TestCase("[13.0.3]", ExpectedResult = false)]
        [TestCase("latest", ExpectedResult = false)]
        public bool IsPlainVersion_IsTrueOnlyForOneVersion(string versionText)
        {
            return VersionResolver.IsPlainVersion(versionText);
        }

        // Checked as written, so it needs no version list, and is not changed by one
        [TestCase("12.0.1")]
        [TestCase("13.0.3")]
        public void Resolve_APlainVersion_IsItself(string versionText)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(VersionResolver.Resolve(versionText, null), Is.EqualTo(NuGetVersion.Parse(versionText)));
                Assert.That(VersionResolver.Resolve(versionText, Available), Is.EqualTo(NuGetVersion.Parse(versionText)));
            }
        }

        // A floating version picks the newest it allows, as restore does, and a range the lowest in it
        [TestCase("12.0.*", "12.0.3", Description = "newest 12.0.x; 12.0.3-beta2 and 13.0.1 left out")]
        [TestCase("9.0.2.*", "9.0.2.60", Description = "a four-part float, compared as numbers: 60 is above 9")]
        [TestCase("12.0.3-*", "12.0.3", Description = "a pre-release float: the release beats its own pre-releases")]
        [TestCase("12.0.2-*", "12.0.2", Description = "no pre-release of 12.0.2 is listed, so the release")]
        [TestCase("*", "13.0.1", Description = "the newest release of all")]
        [TestCase("[4.0.0, 4.5.0]", "4.0.0", Description = "a range: its lowest available version")]
        [TestCase("[4.1.0, )", "4.5.0", Description = "an open range starting between versions: the lowest above")]
        [TestCase("[13.0.1]", "13.0.1", Description = "an exact range")]
        public void Resolve_AFloatingVersionOrRange_IsTheVersionRestoreWouldPick(string versionText, string expected)
        {
            Assert.That(VersionResolver.Resolve(versionText, Available), Is.EqualTo(NuGetVersion.Parse(expected)));
        }

        [TestCase("99.*", Description = "nothing available matches")]
        [TestCase("[20.0.0, 21.0.0)", Description = "nothing available is in the range")]
        [TestCase("latest", Description = "not a version or range")]
        [TestCase("12.0.1 or later", Description = "not a version or range")]
        public void Resolve_TextThatResolvesToNothing_IsNull(string versionText)
        {
            Assert.That(VersionResolver.Resolve(versionText, Available), Is.Null);
        }

        // The package is not on nuget.org (GetVersionsAsync returned null), so only a plain version can be checked
        [Test]
        public void Resolve_AFloatingVersionWithoutAVersionList_IsNull()
        {
            Assert.That(VersionResolver.Resolve("12.0.*", null), Is.Null);
        }
    }
}
