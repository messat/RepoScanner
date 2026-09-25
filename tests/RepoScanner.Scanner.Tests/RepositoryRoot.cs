using System;
using System.IO;

namespace RepoScanner.Scanner.Tests
{
    // The folder holding RepoScanner.sln, found by walking up from the test's output folder, so a test
    // can read files in the repository wherever it is checked out
    internal static class RepositoryRoot
    {
        public static string FullPath { get; } = Find();

        private static string Find()
        {
            DirectoryInfo? folder = new DirectoryInfo(AppContext.BaseDirectory);
            while (folder != null && !File.Exists(Path.Combine(folder.FullName, "RepoScanner.sln")))
            {
                folder = folder.Parent;
            }

            if (folder == null)
            {
                throw new InvalidOperationException("RepoScanner.sln was not found above " + AppContext.BaseDirectory);
            }

            return folder.FullName;
        }
    }
}
