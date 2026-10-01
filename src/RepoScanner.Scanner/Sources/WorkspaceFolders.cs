using System;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace RepoScanner.Scanner.Sources
{
    // The folders clones go in (#39, Issue 8 design, The folder): a new one for each clone, under one root, so two
    // scans never share a folder. The root is <temp>/reposcanner in the app, the replica's temporary directory in
    // Azure (ADR-016), and a test's own folder in tests.
    public sealed class WorkspaceFolders
    {
        // Every file and folder, including hidden ones such as .git on Windows, which a walk skips by default.
        // Links are not followed, so a link in a cloned repository can never change a file outside it.
        private static readonly EnumerationOptions EveryFileWithoutFollowingLinks = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        private readonly ILogger<WorkspaceFolders> _logger;

        public WorkspaceFolders(string root, ILogger<WorkspaceFolders> logger)
        {
            Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            _logger = logger;
        }

        public string Root { get; }

        // A new, empty folder with a random 12-character name, creating the root if it is missing
        public string Create()
        {
            Directory.CreateDirectory(Root);

            while (true)
            {
                // 6 random bytes is 12 hexadecimal characters: short, for Windows' path length, and never guessed
                string folder = Path.Combine(Root, Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant());

                // Another clone already has this name: vanishingly unlikely, so simply pick again
                if (!Directory.Exists(folder))
                {
                    return Directory.CreateDirectory(folder).FullName;
                }
            }
        }

        // Deletes the folder and everything in it. Logs a warning and returns, rather than throwing, when it cannot,
        // so a failed delete never hides the scan's own result (Issue 8 design, decision 11).
        public void Delete(string folder)
        {
            string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

            // Only a folder Create made: never the root itself, anything deeper, or anything outside it
            if (!string.Equals(Path.GetDirectoryName(fullPath), Root, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Refused to delete {Folder}: it is not directly inside {Root}", fullPath, Root);
                return;
            }

            // A clone that failed was deleted before its error went on
            if (!Directory.Exists(fullPath))
            {
                return;
            }

            try
            {
                // Git marks its pack files read-only on Windows, and Directory.Delete fails on a read-only file
                foreach (string file in Directory.EnumerateFiles(fullPath, "*", EveryFileWithoutFollowingLinks))
                {
                    File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
                }

                // A link inside is deleted as a link; what it points to is left alone
                Directory.Delete(fullPath, recursive: true);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                _logger.LogWarning("Could not delete {Folder}: {Reason}", fullPath, exception.Message);
            }
        }
    }
}
