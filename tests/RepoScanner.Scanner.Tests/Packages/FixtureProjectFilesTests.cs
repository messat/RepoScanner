using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RepoScanner.Scanner.Files;
using RepoScanner.Scanner.Packages;

namespace RepoScanner.Scanner.Tests.Packages
{
    // tests/fixtures/packages-repo (#43): what the reader must find in it. The findings these give once
    // they are checked against nuget.org's data are the package scanner's tests (#46).
    [TestFixture]
    public class FixtureProjectFilesTests
    {
        public static string FolderPath { get; } = Path.Combine(RepositoryRoot.FullPath, "tests", "fixtures", "packages-repo");

        private ProjectFileReadResult _result = null!;

        [OneTimeSetUp]
        public async Task ReadTheFixture()
        {
            ProjectFileReader reader = new ProjectFileReader(
                new RepositoryFileWalker(NullLogger<RepositoryFileWalker>.Instance), NullLogger<ProjectFileReader>.Instance);

            _result = await reader.ReadAsync(FolderPath, CancellationToken.None);
        }

        [Test]
        public void ReadAsync_OnTheFixture_DeclaresExactlyThesePackages()
        {
            DeclaredPackage[] expected =
            {
                // Used by Api and Worker without a version of their own, so declared once, on the props file.
                // Newtonsoft.Json and System.Text.Encodings.Web in the same file are used by no project.
                new DeclaredPackage("central/Directory.Packages.props", 6, "Microsoft.Identity.Client", "4.59.0"),
                new DeclaredPackage("central/Worker/Worker.csproj", 9, "System.Text.Encodings.Web", "4.5.0"),

                // The old-style plug-in project: MSBuild 2003 namespace, a <Version> child element, a floating version
                new DeclaredPackage("legacy/Plugins/Plugins.csproj", 16, "Newtonsoft.Json", "13.0.3"),
                new DeclaredPackage("legacy/Plugins/Plugins.csproj", 19, "Microsoft.CrmSdk.CoreAssemblies", "9.0.2.*"),
                new DeclaredPackage("legacy/Plugins/Plugins.csproj", 20, "Microsoft.NETFramework.ReferenceAssemblies", "1.0.0"),

                new DeclaredPackage("plain/Plain.csproj", 9, "Newtonsoft.Json", "12.0.*"),
                new DeclaredPackage("plain/Plain.csproj", 11, "System.Text.Json", "8.0.3")
            };

            Assert.That(_result.Packages, Is.EqualTo(expected));
        }

        [Test]
        public void ReadAsync_OnTheFixture_CountsWhatItSkipped()
        {
            using (Assert.EnterMultipleScope())
            {
                // Api, Worker and the props file under central/, Plugins, Plain and Broken
                Assert.That(_result.ProjectFileCount, Is.EqualTo(6));
                Assert.That(_result.InvalidFileCount, Is.EqualTo(1), "broken/Broken.csproj");
                Assert.That(_result.PropertyVersionCount, Is.EqualTo(1), "Serilog $(SerilogVersion) in plain/Plain.csproj");
                Assert.That(_result.MissingVersionCount, Is.Zero);
            }
        }
    }
}
