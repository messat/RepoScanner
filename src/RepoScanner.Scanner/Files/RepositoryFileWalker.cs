using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RepoScanner.Scanner.Files
{
    // Lists the files a scanner should read under a folder, leaving out what nobody wrote by hand:
    // tool folders, large and binary files, links, folders marked as excluded, and files and folders that
    // cannot be read (Issue 3 design, Skip rules). It also reads the files, so every scanner handles a
    // file that cannot be read the same way. Shared by the secret, package and code-pattern scanners.
    public sealed class RepositoryFileWalker
    {
        // A folder holding this file is skipped, with everything below it, when it is below the scan
        // root. RepoScanner's own fake test secrets sit in such folders, so the nightly scan of this
        // repository does not report them.
        public const string ExclusionMarkerFileName = ".reposcanner-exclude";

        // Larger files are data or generated, not code or configuration (#68)
        public const long MaxFileSizeBytes = 1024 * 1024;

        // A zero byte in the first 8,000 bytes marks a file as binary: the test git uses (#68)
        public const int BinaryCheckByteCount = 8000;

        // Git internals, build output, downloaded packages, and IDE and test state
        private static readonly HashSet<string> SkippedFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", "bin", "obj", "node_modules", "packages", ".vs", "TestResults"
        };

        private readonly ILogger<RepositoryFileWalker> _logger;

        public RepositoryFileWalker(ILogger<RepositoryFileWalker> logger)
        {
            _logger = logger;
        }

        // Sorted by RelativePath (ordinal), so every scan of the same folder lists files in the same order
        public IReadOnlyList<RepositoryFile> GetFiles(string rootPath)
        {
            DirectoryInfo root = new DirectoryInfo(Path.GetFullPath(rootPath));
            List<RepositoryFile> files = new List<RepositoryFile>();
            SkipCounts skipped = new SkipCounts();

            AddFiles(root, root.FullName, isRoot: true, files, skipped);
            files.Sort((first, second) => string.CompareOrdinal(first.RelativePath, second.RelativePath));

            _logger.LogInformation(
                "Found {FileCount} files to scan under {RootPath}; skipped {FolderCount} tool folders, {ExcludedCount} excluded folders, " +
                "{UnreadableFolderCount} unreadable folders, {LinkCount} links, {LargeCount} large files, {BinaryCount} binary files " +
                "and {UnreadableCount} unreadable files",
                files.Count, root.FullName, skipped.ToolFolders, skipped.ExcludedFolders, skipped.UnreadableFolders, skipped.Links,
                skipped.LargeFiles, skipped.BinaryFiles, skipped.UnreadableFiles);

            return files;
        }

        // The file's text, or null when it cannot be read now: it may have been locked or deleted since the
        // walk listed it, and one such file must not stop the scan. UTF-8, honouring a byte order mark;
        // bytes that are not valid UTF-8 become U+FFFD rather than failing, so the rest is still scanned.
        public async Task<string?> TryReadTextAsync(RepositoryFile file, CancellationToken cancellationToken)
        {
            try
            {
                using (FileStream stream = OpenShared(file.FullPath))
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                {
                    return await reader.ReadToEndAsync(cancellationToken);
                }
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                _logger.LogWarning("Skipped {FilePath}: it could not be read ({Reason})", file.RelativePath, exception.Message);
                return null;
            }
        }

        private void AddFiles(DirectoryInfo folder, string rootPath, bool isRoot, List<RepositoryFile> files, SkipCounts skipped)
        {
            // The marker in the scan root itself is ignored, so --path on the fixture still scans it
            if (!isRoot && File.Exists(Path.Combine(folder.FullName, ExclusionMarkerFileName)))
            {
                skipped.ExcludedFolders++;
                return;
            }

            FileInfo[] folderFiles;
            DirectoryInfo[] subfolders;
            try
            {
                // Listed in one go, so a folder that cannot be listed (no permission, or deleted during the
                // walk) is caught here and skipped, rather than stopping the whole scan
                folderFiles = folder.GetFiles();
                subfolders = folder.GetDirectories();
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                _logger.LogWarning("Skipped folder {FolderPath}: it could not be listed ({Reason})", RelativePathOf(rootPath, folder.FullName), exception.Message);
                skipped.UnreadableFolders++;
                return;
            }

            foreach (FileInfo file in folderFiles)
            {
                string relativePath = RelativePathOf(rootPath, file.FullName);

                if (file.LinkTarget != null)
                {
                    // A link could point outside the folder being scanned
                    skipped.Links++;
                }
                else if (file.Length > MaxFileSizeBytes)
                {
                    skipped.LargeFiles++;
                }
                else
                {
                    bool? binary = StartsLikeABinaryFile(file, relativePath);
                    if (binary == null)
                    {
                        skipped.UnreadableFiles++;
                    }
                    else if (binary.Value)
                    {
                        skipped.BinaryFiles++;
                    }
                    else
                    {
                        files.Add(new RepositoryFile(file.FullName, relativePath));
                    }
                }
            }

            foreach (DirectoryInfo child in subfolders)
            {
                if (child.LinkTarget != null)
                {
                    skipped.Links++;
                }
                else if (SkippedFolderNames.Contains(child.Name))
                {
                    skipped.ToolFolders++;
                }
                else
                {
                    AddFiles(child, rootPath, isRoot: false, files, skipped);
                }
            }
        }

        // With / between folders, as a finding records it
        private static string RelativePathOf(string rootPath, string fullPath)
        {
            return Path.GetRelativePath(rootPath, fullPath).Replace(Path.DirectorySeparatorChar, '/');
        }

        // Read-only, but letting other programs go on writing to or deleting the file, so a log file an
        // application has open is still scanned. Only a file another program has locked for itself fails.
        private static FileStream OpenShared(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }

        // True for a binary file, false for text, and null when the file cannot be opened
        private bool? StartsLikeABinaryFile(FileInfo file, string relativePath)
        {
            try
            {
                using (FileStream stream = OpenShared(file.FullName))
                {
                    byte[] start = new byte[BinaryCheckByteCount];
                    int read = stream.ReadAtLeast(start, BinaryCheckByteCount, throwOnEndOfStream: false);
                    return Array.IndexOf(start, (byte)0, 0, read) >= 0;
                }
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                // For example another program has it locked. The rest of the folder is still scanned.
                _logger.LogWarning("Skipped {FilePath}: it could not be opened ({Reason})", relativePath, exception.Message);
                return null;
            }
        }

        // How many of each kind of thing were skipped, for the one summary log line
        private sealed class SkipCounts
        {
            public int ToolFolders { get; set; }

            public int ExcludedFolders { get; set; }

            public int UnreadableFolders { get; set; }

            public int Links { get; set; }

            public int LargeFiles { get; set; }

            public int BinaryFiles { get; set; }

            public int UnreadableFiles { get; set; }
        }
    }
}
