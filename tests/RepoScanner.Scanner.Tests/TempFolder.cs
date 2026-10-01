using System;
using System.IO;

namespace RepoScanner.Scanner.Tests
{
    // A new, empty folder under the system temp folder for one test, deleted when the test ends
    internal sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            FullPath = Path.Combine(Path.GetTempPath(), "reposcanner-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(FullPath);
        }

        public string FullPath { get; }

        // Writes a file at a path relative to this folder (with / between folders), creating the
        // folders it needs, and returns its full path
        public string WriteText(string relativePath, string content)
        {
            string fullPath = PrepareFile(relativePath);
            File.WriteAllText(fullPath, content);
            return fullPath;
        }

        public string WriteBytes(string relativePath, byte[] content)
        {
            string fullPath = PrepareFile(relativePath);
            File.WriteAllBytes(fullPath, content);
            return fullPath;
        }

        public void Dispose()
        {
            // A git repository inside (Issue 8) has read-only files on Windows, which Directory.Delete cannot delete.
            // Hidden files such as .git are included, and links are not followed, so nothing outside is changed.
            EnumerationOptions everyFileWithoutFollowingLinks = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            foreach (string file in Directory.EnumerateFiles(FullPath, "*", everyFileWithoutFollowingLinks))
            {
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            }

            // A symbolic link inside is deleted as a link; what it points to is left alone
            Directory.Delete(FullPath, recursive: true);
        }

        private string PrepareFile(string relativePath)
        {
            string fullPath = Path.Combine(FullPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            return fullPath;
        }
    }
}
