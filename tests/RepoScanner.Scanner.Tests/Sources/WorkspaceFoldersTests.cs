using System;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using NUnit.Framework;
using RepoScanner.Scanner.Sources;

namespace RepoScanner.Scanner.Tests.Sources
{
    // The folders clones go in (#39, Issue 8 design, The folder): a new one for each clone, always deleted
    [TestFixture]
    public class WorkspaceFoldersTests
    {
        private TempFolder _temp = null!;
        private FakeLogger<WorkspaceFolders> _logger = null!;

        [SetUp]
        public void SetUp()
        {
            _temp = new TempFolder();
            _logger = new FakeLogger<WorkspaceFolders>();
        }

        [TearDown]
        public void TearDown()
        {
            _temp.Dispose();
        }

        // The root does not exist yet, as on a new machine or a new replica, so the first Create makes it
        [Test]
        public void Create_GivesANewEmptyFolderDirectlyInsideTheRoot()
        {
            WorkspaceFolders folders = Folders();

            string folder = folders.Create();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Directory.Exists(folder), Is.True);
                Assert.That(Path.GetDirectoryName(folder), Is.EqualTo(folders.Root));
                Assert.That(Path.GetFileName(folder), Does.Match("^[0-9a-f]{12}$"));
                Assert.That(Directory.EnumerateFileSystemEntries(folder), Is.Empty);
            }
        }

        // Two scans never share a folder
        [Test]
        public void Create_Twice_GivesTwoDifferentFolders()
        {
            WorkspaceFolders folders = Folders();

            string first = folders.Create();
            string second = folders.Create();

            Assert.That(second, Is.Not.EqualTo(first));
        }

        // Decision 11: git marks its pack files read-only on Windows, and Directory.Delete then fails. The .git
        // folder is hidden on Windows, which a directory walk skips unless told not to.
        [Test]
        public void Delete_RemovesTheFolderEvenWithReadOnlyFilesInside()
        {
            WorkspaceFolders folders = Folders();
            string folder = folders.Create();
            string git = Directory.CreateDirectory(Path.Combine(folder, ".git")).FullName;
            string pack = Path.Combine(Directory.CreateDirectory(Path.Combine(git, "objects", "pack")).FullName, "pack-1.pack");
            File.WriteAllText(pack, "pack");
            File.SetAttributes(pack, FileAttributes.ReadOnly);
            File.SetAttributes(git, File.GetAttributes(git) | FileAttributes.Hidden);

            folders.Delete(folder);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Directory.Exists(folder), Is.False);
                Assert.That(_logger.Collector.Count, Is.Zero, "a warning was logged");
            }
        }

        // A link in a cloned repository must never change a file outside it
        [Test]
        public void Delete_DoesNotFollowALinkToAFolderOutside()
        {
            string keep = _temp.WriteText("outside/keep.txt", "keep");
            File.SetAttributes(keep, FileAttributes.ReadOnly);
            WorkspaceFolders folders = Folders();
            string folder = folders.Create();
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(folder, "link"), Path.GetDirectoryName(keep)!);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                Assert.Ignore("Symbolic links need Developer Mode or administrator rights on this machine");
            }

            folders.Delete(folder);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Directory.Exists(folder), Is.False);
                Assert.That(File.Exists(keep), Is.True);
                Assert.That(File.GetAttributes(keep).HasFlag(FileAttributes.ReadOnly), Is.True, "the walk followed the link");
            }
        }

        // A guard against ever deleting the wrong folder: only a folder Create made, directly inside the root
        [TestCase("elsewhere")]
        [TestCase("reposcanner")]
        [TestCase("reposcanner/3f9c0a1b7d2e/nested")]
        public void Delete_ForAFolderNotDirectlyInsideTheRoot_LeavesItAndWarns(string relativePath)
        {
            string other = Directory.CreateDirectory(Path.Combine(_temp.FullPath, relativePath)).FullName;

            Folders().Delete(other);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Directory.Exists(other), Is.True);
                Assert.That(_logger.LatestRecord.Level, Is.EqualTo(LogLevel.Warning));
                Assert.That(_logger.LatestRecord.Message, Does.StartWith("Refused to delete"));
            }
        }

        // A clone that failed is deleted before its error goes on, so there may be nothing left to delete
        [Test]
        public void Delete_ForAFolderAlreadyGone_DoesNothing()
        {
            WorkspaceFolders folders = Folders();
            string folder = folders.Create();
            Directory.Delete(folder);

            folders.Delete(folder);

            Assert.That(_logger.Collector.Count, Is.Zero, "a warning was logged");
        }

        // A failed delete must not hide the scan's own result (decision 11). Windows only: Linux deletes an open file.
        [Test, Platform(Include = "Win")]
        public void Delete_WhenAFileIsLocked_WarnsInsteadOfThrowing()
        {
            WorkspaceFolders folders = Folders();
            string folder = folders.Create();

            using (FileStream locked = new FileStream(Path.Combine(folder, "locked.txt"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.DoesNotThrow(() => folders.Delete(folder));
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_logger.LatestRecord.Level, Is.EqualTo(LogLevel.Warning));
                Assert.That(_logger.LatestRecord.Message, Does.StartWith("Could not delete"));
            }
        }

        private WorkspaceFolders Folders()
        {
            return new WorkspaceFolders(Path.Combine(_temp.FullPath, "reposcanner"), _logger);
        }
    }
}
