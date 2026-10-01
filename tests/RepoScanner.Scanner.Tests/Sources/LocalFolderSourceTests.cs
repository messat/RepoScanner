using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using RepoScanner.Scanner.Configuration;
using RepoScanner.Scanner.Queue;
using RepoScanner.Scanner.Sources;

namespace RepoScanner.Scanner.Tests.Sources
{
    [TestFixture]
    public class LocalFolderSourceTests
    {
        private TempFolder _root = null!;

        [SetUp]
        public void SetUp()
        {
            _root = new TempFolder();
        }

        [TearDown]
        public void TearDown()
        {
            _root.Dispose();
        }

        [Test]
        public async Task OpenAsync_ForAFolderInLocalRoot_GivesThatFolder()
        {
            string expected = Directory.CreateDirectory(Path.Combine(_root.FullPath, "orders-api")).FullName;

            await using (RepositoryWorkspace workspace = await Source().OpenAsync(Request("orders-api"), CancellationToken.None))
            {
                Assert.That(workspace.Folder, Is.EqualTo(expected));
            }
        }

        // Ends as a Failed scan run with this as its error, retried, then poisoned (Issue 7 design)
        [Test]
        public void OpenAsync_ForAMissingFolder_SaysWhichAndWhere()
        {
            DirectoryNotFoundException exception = Assert.ThrowsAsync<DirectoryNotFoundException>(
                () => Source().OpenAsync(Request("Ghost"), CancellationToken.None));

            Assert.That(exception.Message, Is.EqualTo("No folder named Ghost in " + _root.FullPath));
        }

        // ScanRequestReader already refuses these names; this is the second check, where the folder is opened
        [Test]
        public void OpenAsync_ForANameThatPointsAboveLocalRoot_Refuses()
        {
            Assert.ThrowsAsync<ArgumentException>(() => Source().OpenAsync(Request(".."), CancellationToken.None));
        }

        [Test]
        public void OpenAsync_ForAFolderDeeperInsideLocalRoot_Refuses()
        {
            Directory.CreateDirectory(Path.Combine(_root.FullPath, "team", "orders-api"));

            Assert.ThrowsAsync<ArgumentException>(() => Source().OpenAsync(Request(Path.Combine("team", "orders-api")), CancellationToken.None));
        }

        // The folder is the developer's own repository: closing the workspace must never delete it
        [Test]
        public async Task OpenAsync_ClosingTheWorkspace_LeavesTheFolder()
        {
            string folder = Directory.CreateDirectory(Path.Combine(_root.FullPath, "orders-api")).FullName;
            File.WriteAllText(Path.Combine(folder, "Program.cs"), "// code");

            RepositoryWorkspace workspace = await Source().OpenAsync(Request("orders-api"), CancellationToken.None);
            await workspace.DisposeAsync();

            Assert.That(File.Exists(Path.Combine(folder, "Program.cs")), Is.True);
        }

        private LocalFolderSource Source()
        {
            return new LocalFolderSource(Options.Create(new SourceOptions { LocalRoot = _root.FullPath }));
        }

        private static ScanRequest Request(string repositoryName)
        {
            return new ScanRequest(Guid.NewGuid(), Guid.NewGuid(), "Lumina", repositoryName, "develop");
        }
    }
}
