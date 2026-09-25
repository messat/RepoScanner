using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;

namespace RepoScanner.Scanner.Files
{
    // Lists the files a scanner should read under a folder, leaving out what nobody wrote by hand:
    // tool folders, large and binary files, links, folders marked as excluded, and files that cannot be
    // opened (Issue 3 design, Skip rules). Shared by the secret, package and code-pattern scanners.
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
                "{LinkCount} links, {LargeCount} large files, {BinaryCount} binary files and {UnreadableCount} unreadable files",
                files.Count, root.FullName, skipped.ToolFolders, skipped.ExcludedFolders, skipped.Links,
                skipped.LargeFiles, skipped.BinaryFiles, skipped.UnreadableFiles);

            return files;
        }

        private void AddFiles(DirectoryInfo folder, string rootPath, bool isRoot, List<RepositoryFile> files, SkipCounts skipped)
        {
            // The marker in the scan root itself is ignored, so --path on the fixture still scans it
            if (!isRoot && File.Exists(Path.Combine(folder.FullName, ExclusionMarkerFileName)))
            {
                skipped.ExcludedFolders++;
                return;
            }

            foreach (FileInfo file in folder.EnumerateFiles())
            {
                string relativePath = Path.GetRelativePath(rootPath, file.FullName).Replace(Path.DirectorySeparatorChar, '/');

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

            foreach (DirectoryInfo child in folder.EnumerateDirectories())
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

        // True for a binary file, false for text, and null when the file cannot be opened
        private bool? StartsLikeABinaryFile(FileInfo file, string relativePath)
        {
            try
            {
                using (FileStream stream = file.OpenRead())
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

            public int Links { get; set; }

            public int LargeFiles { get; set; }

            public int BinaryFiles { get; set; }

            public int UnreadableFiles { get; set; }
        }
    }
}
