using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using NUnit.Framework;
using RepoScanner.Scanner.Files;

namespace RepoScanner.Scanner.Tests.Files
{
    [TestFixture]
    public class RepositoryFileWalkerTests
    {
        private TempFolder _folder = null!;
        private FakeLogger<RepositoryFileWalker> _logger = null!;

        [SetUp]
        public void SetUp()
        {
            _folder = new TempFolder();
            _logger = new FakeLogger<RepositoryFileWalker>();
        }

        [TearDown]
        public void TearDown()
        {
            _folder.Dispose();
        }

        [Test]
        public void GetFiles_ListsFilesInEveryFolderWithForwardSlashesInOrder()
        {
            _folder.WriteText("b.txt", "x");
            _folder.WriteText("a/z.txt", "x");
            _folder.WriteText("a/b/c.json", "x");

            Assert.That(RelativePaths(), Is.EqualTo(new string[] { "a/b/c.json", "a/z.txt", "b.txt" }));
        }

        [Test]
        public void GetFiles_GivesTheFullPathOfEachFile()
        {
            string fullPath = _folder.WriteText("src/app.cs", "x");

            Assert.That(Walker().GetFiles(_folder.FullPath).Single().FullPath, Is.EqualTo(fullPath));
        }

        [Test]
        public void GetFiles_OnAnEmptyFolder_ListsNothing()
        {
            Assert.That(RelativePaths(), Is.Empty);
        }

        [TestCase(".git")]
        [TestCase("bin")]
        [TestCase("obj")]
        [TestCase("node_modules")]
        [TestCase("packages")]
        [TestCase(".vs")]
        [TestCase("TestResults")]
        [TestCase("BIN")]
        public void GetFiles_SkipsToolFoldersAtAnyDepth(string folderName)
        {
            _folder.WriteText(folderName + "/settings.json", "x");
            _folder.WriteText("src/" + folderName + "/deep.json", "x");
            _folder.WriteText("keep.txt", "x");

            Assert.That(RelativePaths(), Is.EqualTo(new string[] { "keep.txt" }));
        }

        // Exactly 1 MB is scanned; one byte more is not (#68)
        [Test]
        public void GetFiles_SkipsFilesLargerThanOneMegabyte()
        {
            _folder.WriteBytes("exact.txt", Filled(RepositoryFileWalker.MaxFileSizeBytes));
            _folder.WriteBytes("larger.txt", Filled(RepositoryFileWalker.MaxFileSizeBytes + 1));

            Assert.That(RelativePaths(), Is.EqualTo(new string[] { "exact.txt" }));
        }

        // A zero byte in the first 8,000 bytes means binary; one after them does not (#68)
        [Test]
        public void GetFiles_SkipsFilesWithAZeroByteInTheFirst8000Bytes()
        {
            byte[] early = Filled(100);
            early[50] = 0;
            byte[] late = Filled(9000);
            late[RepositoryFileWalker.BinaryCheckByteCount] = 0;
            _folder.WriteBytes("early.bin", early);
            _folder.WriteBytes("late.txt", late);

            Assert.That(RelativePaths(), Is.EqualTo(new string[] { "late.txt" }));
        }

        [Test]
        public void GetFiles_SkipsAFolderBelowTheRootThatHasTheMarker()
        {
            _folder.WriteText("vendor/" + RepositoryFileWalker.ExclusionMarkerFileName, string.Empty);
            _folder.WriteText("vendor/lib/settings.json", "x");
            _folder.WriteText("src/app.cs", "x");

            Assert.That(RelativePaths(), Is.EqualTo(new string[] { "src/app.cs" }));
        }

        // So --path on the fixture still scans it, although the fixture carries the marker
        [Test]
        public void GetFiles_IgnoresTheMarkerInTheRootItself()
        {
            _folder.WriteText(RepositoryFileWalker.ExclusionMarkerFileName, string.Empty);
            _folder.WriteText("src/app.cs", "x");

            Assert.That(RelativePaths(), Is.EqualTo(new string[] { RepositoryFileWalker.ExclusionMarkerFileName, "src/app.cs" }));
        }

        // A cloned repository could link to a file outside itself
        [Test]
        public void GetFiles_DoesNotFollowSymbolicLinks()
        {
            using (TempFolder outside = new TempFolder())
            {
                string outsideFile = outside.WriteText("outside.txt", "x");
                _folder.WriteText("real.txt", "x");
                try
                {
                    File.CreateSymbolicLink(Path.Combine(_folder.FullPath, "linked.txt"), outsideFile);
                    Directory.CreateSymbolicLink(Path.Combine(_folder.FullPath, "linked-folder"), outside.FullPath);
                }
                catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
                {
                    // Windows lets only an administrator, or a machine in Developer Mode, create links
                    Assert.Inconclusive("This machine does not let the test create symbolic links: " + exception.Message);
                }

                Assert.That(RelativePaths(), Is.EqualTo(new string[] { "real.txt" }));
            }
        }

        // Review focus: a file another program has locked must not stop the scan
        [Test]
        public void GetFiles_SkipsAFileItCannotOpenAndLogsAWarningNamingIt()
        {
            string lockedPath = _folder.WriteText("locked.log", "x");
            _folder.WriteText("open.txt", "x");

            IReadOnlyList<string> paths;
            using (FileStream locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                paths = RelativePaths();
            }

            FakeLogRecord warning = _logger.Collector.GetSnapshot().Single(record => record.Level == LogLevel.Warning);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(paths, Is.EqualTo(new string[] { "open.txt" }));
                Assert.That(warning.Message, Does.Contain("locked.log"));
            }
        }

        // Review fix: a log file an application is still writing can be read, so it must be scanned
        [Test]
        public void GetFiles_ListsAFileAnotherProgramHasOpenForWriting()
        {
            string logPath = _folder.WriteText("app.log", "x");

            IReadOnlyList<string> paths;
            using (FileStream writer = new FileStream(logPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                paths = RelativePaths();
            }

            Assert.That(paths, Is.EqualTo(new string[] { "app.log" }));
        }

        // Review fix: a folder that cannot be listed is skipped, and the rest of the scan goes on
        [Test]
        public void GetFiles_SkipsAFolderItCannotListAndLogsAWarningNamingIt()
        {
            _folder.WriteText("private/data.json", "x");
            _folder.WriteText("open.txt", "x");

            IReadOnlyList<string> paths;
            using (FolderListingDenied denied = FolderListingDenied.On(Path.Combine(_folder.FullPath, "private")))
            {
                if (!denied.Applied)
                {
                    Assert.Inconclusive("This user can list any folder (for example root on Linux), so the test cannot deny it");
                }

                paths = RelativePaths();
            }

            FakeLogRecord warning = _logger.Collector.GetSnapshot().Single(record => record.Level == LogLevel.Warning);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(paths, Is.EqualTo(new string[] { "open.txt" }));
                Assert.That(warning.Message, Does.Contain("private"));
                Assert.That(_logger.LatestRecord.GetStructuredStateValue("UnreadableFolderCount"), Is.EqualTo("1"));
            }
        }

        [Test]
        public async Task TryReadTextAsync_ReturnsTheFilesText()
        {
            _folder.WriteText("settings.json", "{ \"a\": 1 }");
            RepositoryFile file = Walker().GetFiles(_folder.FullPath).Single();

            string? text = await Walker().TryReadTextAsync(file, CancellationToken.None);

            Assert.That(text, Is.EqualTo("{ \"a\": 1 }"));
        }

        // Review fix: the walk lists every file first, so a file can go between the walk and the read
        [Test]
        public async Task TryReadTextAsync_WhenTheFileWasDeletedSinceTheWalk_ReturnsNullAndLogsAWarning()
        {
            string path = _folder.WriteText("gone.json", "x");
            RepositoryFile file = Walker().GetFiles(_folder.FullPath).Single();
            File.Delete(path);

            string? text = await Walker().TryReadTextAsync(file, CancellationToken.None);

            FakeLogRecord warning = _logger.Collector.GetSnapshot().Single(record => record.Level == LogLevel.Warning);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(text, Is.Null);
                Assert.That(warning.Message, Does.Contain("gone.json"));
            }
        }

        // Review fix: a file whose first 8,000 bytes can be read passes the walk, but another program
        // has locked a later part of it. Only Windows makes other readers respect such a lock.
        [Test]
        public async Task TryReadTextAsync_WhenPartOfTheFileIsLocked_ReturnsNullAndLogsAWarning()
        {
            if (OperatingSystem.IsWindows())
            {
                string path = _folder.WriteBytes("big.txt", Filled(20000));
                RepositoryFile file = Walker().GetFiles(_folder.FullPath).Single();

                string? text;
                using (FileStream holder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    holder.Lock(10000, 1000);
                    text = await Walker().TryReadTextAsync(file, CancellationToken.None);
                }

                FakeLogRecord warning = _logger.Collector.GetSnapshot().Single(record => record.Level == LogLevel.Warning);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(text, Is.Null);
                    Assert.That(warning.Message, Does.Contain("big.txt"));
                }
            }
            else
            {
                Assert.Ignore("Only Windows makes other readers respect a lock on part of a file");
            }
        }

        [Test]
        public void GetFiles_LogsHowManyItSkippedForEachReason()
        {
            byte[] binary = Filled(10);
            binary[0] = 0;
            _folder.WriteText("bin/tool.dll.config", "x");
            _folder.WriteText("vendor/" + RepositoryFileWalker.ExclusionMarkerFileName, string.Empty);
            _folder.WriteBytes("large.txt", Filled(RepositoryFileWalker.MaxFileSizeBytes + 1));
            _folder.WriteBytes("image.png", binary);
            _folder.WriteText("app.cs", "x");

            RelativePaths();

            FakeLogRecord summary = _logger.LatestRecord;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(summary.GetStructuredStateValue("FileCount"), Is.EqualTo("1"));
                Assert.That(summary.GetStructuredStateValue("FolderCount"), Is.EqualTo("1"));
                Assert.That(summary.GetStructuredStateValue("ExcludedCount"), Is.EqualTo("1"));
                Assert.That(summary.GetStructuredStateValue("LargeCount"), Is.EqualTo("1"));
                Assert.That(summary.GetStructuredStateValue("BinaryCount"), Is.EqualTo("1"));
                Assert.That(summary.GetStructuredStateValue("LinkCount"), Is.EqualTo("0"));
                Assert.That(summary.GetStructuredStateValue("UnreadableCount"), Is.EqualTo("0"));
                Assert.That(summary.GetStructuredStateValue("UnreadableFolderCount"), Is.EqualTo("0"));
            }
        }

        private IReadOnlyList<string> RelativePaths()
        {
            return Walker().GetFiles(_folder.FullPath).Select(file => file.RelativePath).ToList();
        }

        private RepositoryFileWalker Walker()
        {
            return new RepositoryFileWalker(_logger);
        }

        private static byte[] Filled(long length)
        {
            byte[] bytes = new byte[length];
            Array.Fill(bytes, (byte)'a');
            return bytes;
        }
    }
}
