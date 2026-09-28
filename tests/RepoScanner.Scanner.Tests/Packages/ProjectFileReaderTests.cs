using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using NUnit.Framework;
using RepoScanner.Scanner.Files;
using RepoScanner.Scanner.Packages;

namespace RepoScanner.Scanner.Tests.Packages
{
    [TestFixture]
    public class ProjectFileReaderTests
    {
        private TempFolder _folder = null!;
        private FakeLogger<ProjectFileReader> _logger = null!;

        [SetUp]
        public void SetUp()
        {
            _folder = new TempFolder();
            _logger = new FakeLogger<ProjectFileReader>();
        }

        [TearDown]
        public void TearDown()
        {
            _folder.Dispose();
        }

        [Test]
        public async Task ReadAsync_APackageReferenceWithAVersionAttribute_IsDeclaredOnItsLine()
        {
            _folder.WriteText("src/Api/Api.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
                "  <ItemGroup>\n" +
                "    <PackageReference Include=\"Newtonsoft.Json\" Version=\"12.0.1\" />\n" +
                "  </ItemGroup>\n" +
                "</Project>\n");

            ProjectFileReadResult result = await ReadAsync();

            Assert.That(result.Packages, Is.EqualTo(new[] { new DeclaredPackage("src/Api/Api.csproj", 3, "Newtonsoft.Json", "12.0.1") }));
        }

        // As in an old-style Dynamics 365 plug-in project, which writes <Version>13.0.3</Version>
        [Test]
        public async Task ReadAsync_AVersionChildElement_IsTheVersion()
        {
            _folder.WriteText("Plugins/Plugins.csproj",
                "<Project>\n" +
                "  <ItemGroup>\n" +
                "    <PackageReference Include=\"Newtonsoft.Json\">\n" +
                "      <Version> 13.0.3 </Version>\n" +
                "    </PackageReference>\n" +
                "  </ItemGroup>\n" +
                "</Project>\n");

            ProjectFileReadResult result = await ReadAsync();

            Assert.That(result.Packages, Is.EqualTo(new[] { new DeclaredPackage("Plugins/Plugins.csproj", 3, "Newtonsoft.Json", "13.0.3") }));
        }

        [Test]
        public async Task ReadAsync_AVersionOverrideAttribute_WinsOverTheVersion()
        {
            _folder.WriteText("Worker.csproj",
                "<Project>\n" +
                "  <ItemGroup>\n" +
                "    <PackageReference Include=\"System.Text.Encodings.Web\" Version=\"8.0.0\" VersionOverride=\"4.5.0\" />\n" +
                "  </ItemGroup>\n" +
                "</Project>\n");

            ProjectFileReadResult result = await ReadAsync();

            Assert.That(result.Packages.Single().VersionText, Is.EqualTo("4.5.0"));
        }

        [Test]
        public async Task ReadAsync_AVersionOverrideChildElement_WinsOverTheVersion()
        {
            _folder.WriteText("Worker.csproj",
                "<Project>\n" +
                "  <ItemGroup>\n" +
                "    <PackageReference Include=\"System.Text.Encodings.Web\" Version=\"8.0.0\">\n" +
                "      <VersionOverride>4.5.0</VersionOverride>\n" +
                "    </PackageReference>\n" +
                "  </ItemGroup>\n" +
                "</Project>\n");

            ProjectFileReadResult result = await ReadAsync();

            Assert.That(result.Packages.Single().VersionText, Is.EqualTo("4.5.0"));
        }

        // Old-style projects put every element in the MSBuild 2003 namespace (Issue 5 design, decision 3)
        [Test]
        public async Task ReadAsync_AProjectInTheMSBuild2003Namespace_IsReadTheSame()
        {
            _folder.WriteText("Plugins.csproj",
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                "<Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\n" +
                "  <ItemGroup>\n" +
                "    <Reference Include=\"System\" />\n" +
                "    <PackageReference Include=\"Newtonsoft.Json\">\n" +
                "      <Version>13.0.3</Version>\n" +
                "    </PackageReference>\n" +
                "  </ItemGroup>\n" +
                "</Project>\n");

            ProjectFileReadResult result = await ReadAsync();

            Assert.That(result.Packages, Is.EqualTo(new[] { new DeclaredPackage("Plugins.csproj", 5, "Newtonsoft.Json", "13.0.3") }));
        }

        // Central Package Management: the finding points where the version is set (Issue 5 design, decision 5)
        [Test]
        public async Task ReadAsync_AReferenceWithoutAVersion_IsDeclaredAtItsPackageVersionInTheProps()
        {
            WritePackagesProps("central/Directory.Packages.props", ("Microsoft.Identity.Client", "4.59.0"));
            WriteProject("central/Api/Api.csproj", "<PackageReference Include=\"Microsoft.Identity.Client\" />");

            ProjectFileReadResult result = await ReadAsync();

            Assert.That(result.Packages, Is.EqualTo(new[] { new DeclaredPackage("central/Directory.Packages.props", 3, "Microsoft.Identity.Client", "4.59.0") }));
        }

        [Test]
        public async Task ReadAsync_APackageVersionUsedByTwoProjects_IsDeclaredOnce()
        {
            WritePackagesProps("Directory.Packages.props", ("Microsoft.Identity.Client", "4.59.0"));
            WriteProject("Api/Api.csproj", "<PackageReference Include=\"Microsoft.Identity.Client\" />");
            WriteProject("Worker/Worker.csproj", "<PackageReference Include=\"Microsoft.Identity.Client\" />");

            ProjectFileReadResult result = await ReadAsync();

            Assert.That(result.Packages, Has.Count.EqualTo(1));
        }

        // Nothing restores a version no project uses (Issue 5 design, decision 6)
        [Test]
        public async Task ReadAsync_APackageVersionNoProjectUses_IsNotDeclared()
        {
            WritePackagesProps("Directory.Packages.props", ("Microsoft.Identity.Client", "4.59.0"), ("Newtonsoft.Json", "12.0.1"));
            WriteProject("Api/Api.csproj", "<PackageReference Include=\"Microsoft.Identity.Client\" />");

            ProjectFileReadResult result = await ReadAsync();

            Assert.That(result.Packages.Select(package => package.PackageId), Is.EqualTo(new[] { "Microsoft.Identity.Client" }));
        }

        // MSBuild uses the first Directory.Packages.props it finds walking up from the project's folder
        [Test]
        public async Task ReadAsync_TheNearestProps_WinsOverOneFurtherUp()
        {
            WritePackagesProps("Directory.Packages.props", ("Newtonsoft.Json", "12.0.1"));
            WritePackagesProps("src/Directory.Packages.props", ("Newtonsoft.Json", "13.0.3"));
            WriteProject("src/Api/Api.csproj", "<PackageReference Include=\"Newtonsoft.Json\" />");

            ProjectFileReadResult result = await ReadAsync();

            Assert.That(result.Packages, Is.EqualTo(new[] { new DeclaredPackage("src/Directory.Packages.props", 3, "Newtonsoft.Json", "13.0.3") }));
        }

        // MSBuild imports only the nearest props file, so a version set further up is not seen (NuGet's NU1010)
        [Test]
        public async Task ReadAsync_TheNearestPropsWithoutThePackage_HidesOneFurtherUp()
        {
            WritePackagesProps("Directory.Packages.props", ("Newtonsoft.Json", "12.0.1"));
            WritePackagesProps("src/Directory.Packages.props", ("System.Text.Json", "8.0.3"));
            WriteProject("src/Api/Api.csproj", "<PackageReference Include=\"Newtonsoft.Json\" />");

            ProjectFileReadResult result = await ReadAsync();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.Packages, Is.Empty);
                Assert.That(result.MissingVersionCount, Is.EqualTo(1));
            }
        }

        [Test]
        public async Task ReadAsync_AnEmptyVersion_IsCountedAsMissing()
        {
            WriteProject("Api/Api.csproj", "<PackageReference Include=\"Newtonsoft.Json\" Version=\" \" />");

            ProjectFileReadResult result = await ReadAsync();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.Packages, Is.Empty);
                Assert.That(result.MissingVersionCount, Is.EqualTo(1));
            }
        }

        [Test]
        public async Task ReadAsync_APropsInAnotherBranchOfFolders_IsNotUsed()
        {
            WritePackagesProps("other/Directory.Packages.props", ("Newtonsoft.Json", "12.0.1"));
            WriteProject("src/Api/Api.csproj", "<PackageReference Include=\"Newtonsoft.Json\" />");

            ProjectFileReadResult result = await ReadAsync();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.Packages, Is.Empty);
                Assert.That(result.MissingVersionCount, Is.EqualTo(1));
            }
        }

        [Test]
        public async Task ReadAsync_AReferenceWithNoVersionAnywhere_IsCountedAsMissing()
        {
            WriteProject("Api/Api.csproj", "<PackageReference Include=\"Newtonsoft.Json\" />");

            ProjectFileReadResult result = await ReadAsync();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.Packages, Is.Empty);
                Assert.That(result.MissingVersionCount, Is.EqualTo(1));
            }
        }

        // NuGet package IDs ignore case. The ID is as written where the version is set.
        [Test]
        public async Task ReadAsync_AReferenceInADifferentCase_UsesThePackageVersion()
        {
            WritePackagesProps("Directory.Packages.props", ("Newtonsoft.Json", "12.0.1"));
            WriteProject("Api/Api.csproj", "<PackageReference Include=\"newtonsoft.json\" />");

            ProjectFileReadResult result = await ReadAsync();

            Assert.That(result.Packages.Single().PackageId, Is.EqualTo("Newtonsoft.Json"));
        }

        // A GlobalPackageReference applies to every project below the props file
        [Test]
        public async Task ReadAsync_AGlobalPackageReference_IsDeclaredOnItsLine()
        {
            _folder.WriteText("Directory.Packages.props",
                "<Project>\n" +
                "  <ItemGroup>\n" +
                "    <GlobalPackageReference Include=\"Nerdbank.GitVersioning\" Version=\"3.6.133\" />\n" +
                "  </ItemGroup>\n" +
                "</Project>\n");

            ProjectFileReadResult result = await ReadAsync();

            Assert.That(result.Packages, Is.EqualTo(new[] { new DeclaredPackage("Directory.Packages.props", 3, "Nerdbank.GitVersioning", "3.6.133") }));
        }

        // Update changes a reference made elsewhere; it is not a reference itself
        [Test]
        public async Task ReadAsync_APackageReferenceUpdate_IsIgnored()
        {
            WriteProject("Api/Api.csproj", "<PackageReference Update=\"Newtonsoft.Json\" Version=\"12.0.1\" />");

            ProjectFileReadResult result = await ReadAsync();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.Packages, Is.Empty);
                Assert.That(result.MissingVersionCount, Is.Zero);
            }
        }

        // MSBuild properties are not evaluated, so a version or ID written as one cannot be checked
        [TestCase("<PackageReference Include=\"Serilog\" Version=\"$(SerilogVersion)\" />")]
        [TestCase("<PackageReference Include=\"$(LoggingPackage)\" Version=\"3.1.1\" />")]
        public async Task ReadAsync_AVersionOrIdWrittenAsAProperty_IsCountedNotDeclared(string reference)
        {
            WriteProject("Api/Api.csproj", reference, "<PackageReference Include=\"Newtonsoft.Json\" Version=\"12.0.1\" />");

            ProjectFileReadResult result = await ReadAsync();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.Packages.Select(package => package.PackageId), Is.EqualTo(new[] { "Newtonsoft.Json" }));
                Assert.That(result.PropertyVersionCount, Is.EqualTo(1));
            }
        }

        [Test]
        public async Task ReadAsync_APropertyVersionInThePropsUsedByTwoProjects_IsCountedOnce()
        {
            WritePackagesProps("Directory.Packages.props", ("Serilog", "$(SerilogVersion)"));
            WriteProject("Api/Api.csproj", "<PackageReference Include=\"Serilog\" />");
            WriteProject("Worker/Worker.csproj", "<PackageReference Include=\"Serilog\" />");

            ProjectFileReadResult result = await ReadAsync();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.Packages, Is.Empty);
                Assert.That(result.PropertyVersionCount, Is.EqualTo(1));
            }
        }

        [Test]
        public async Task ReadAsync_AFileThatIsNotValidXml_IsSkippedWithAWarningAndTheOthersAreRead()
        {
            _folder.WriteText("Broken/Broken.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
                "  <ItemGroup>\n" +
                "    <PackageReference Include=\"Newtonsoft.Json\" Version=\"12.0.1\" />\n" +
                "  </ItemGroup>\n");
            WriteProject("Api/Api.csproj", "<PackageReference Include=\"System.Text.Json\" Version=\"8.0.3\" />");

            ProjectFileReadResult result = await ReadAsync();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.Packages.Select(package => package.PackageId), Is.EqualTo(new[] { "System.Text.Json" }));
                Assert.That(result.InvalidFileCount, Is.EqualTo(1));
                Assert.That(_logger.LatestRecord.Level, Is.EqualTo(LogLevel.Warning));
                Assert.That(_logger.LatestRecord.GetStructuredStateValue("FilePath"), Is.EqualTo("Broken/Broken.csproj"));
            }
        }

        // A scanned repository is untrusted: a DTD could expand entities without limit or fetch a URL, so a
        // file with one is refused rather than parsed (Issue 5 design, decision 3)
        [Test]
        public async Task ReadAsync_AFileWithADtd_IsSkippedAndItsEntitiesAreNotExpanded()
        {
            _folder.WriteText("Api/Api.csproj",
                "<?xml version=\"1.0\"?>\n" +
                "<!DOCTYPE Project [ <!ENTITY version \"12.0.1\"> ]>\n" +
                "<Project>\n" +
                "  <ItemGroup>\n" +
                "    <PackageReference Include=\"Newtonsoft.Json\" Version=\"&version;\" />\n" +
                "  </ItemGroup>\n" +
                "</Project>\n");

            ProjectFileReadResult result = await ReadAsync();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.Packages, Is.Empty);
                Assert.That(result.InvalidFileCount, Is.EqualTo(1));
            }
        }

        [Test]
        public async Task ReadAsync_CountsEveryProjectFileFound_IncludingInvalidOnes()
        {
            WritePackagesProps("Directory.Packages.props", ("Newtonsoft.Json", "12.0.1"));
            WriteProject("Api/Api.csproj");
            _folder.WriteText("Broken/Broken.csproj", "<Project>");
            _folder.WriteText("Api/Program.cs", "// not a project file");

            ProjectFileReadResult result = await ReadAsync();

            Assert.That(result.ProjectFileCount, Is.EqualTo(3));
        }

        [Test]
        public async Task ReadAsync_ProjectFileNames_AreMatchedIgnoringCase()
        {
            WritePackagesProps("directory.packages.props", ("Newtonsoft.Json", "12.0.1"));
            WriteProject("Api/API.CSPROJ", "<PackageReference Include=\"Newtonsoft.Json\" />");

            ProjectFileReadResult result = await ReadAsync();

            Assert.That(result.Packages, Is.EqualTo(new[] { new DeclaredPackage("directory.packages.props", 3, "Newtonsoft.Json", "12.0.1") }));
        }

        // The walker's skip rules apply (Issue 3 design): obj holds NuGet's own generated files
        [Test]
        public async Task ReadAsync_AProjectFileInASkippedFolder_IsNotRead()
        {
            WriteProject("Api/obj/Api.csproj.nuget.g.csproj", "<PackageReference Include=\"Newtonsoft.Json\" Version=\"12.0.1\" />");

            ProjectFileReadResult result = await ReadAsync();

            Assert.That(result.ProjectFileCount, Is.Zero);
        }

        // A project whose items are on line 3 onwards
        private void WriteProject(string relativePath, params string[] items)
        {
            _folder.WriteText(relativePath,
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
                "  <ItemGroup>\n" +
                string.Concat(items.Select(item => "    " + item + "\n")) +
                "  </ItemGroup>\n" +
                "</Project>\n");
        }

        // A Directory.Packages.props whose first <PackageVersion> is on line 3
        private void WritePackagesProps(string relativePath, params (string PackageId, string Version)[] versions)
        {
            _folder.WriteText(relativePath,
                "<Project>\n" +
                "  <ItemGroup>\n" +
                string.Concat(versions.Select(version => "    <PackageVersion Include=\"" + version.PackageId + "\" Version=\"" + version.Version + "\" />\n")) +
                "  </ItemGroup>\n" +
                "</Project>\n");
        }

        private Task<ProjectFileReadResult> ReadAsync()
        {
            ProjectFileReader reader = new ProjectFileReader(new RepositoryFileWalker(new FakeLogger<RepositoryFileWalker>()), _logger);
            return reader.ReadAsync(_folder.FullPath, CancellationToken.None);
        }
    }
}
