using System.Collections.Generic;

namespace RepoScanner.Scanner.Packages
{
    // What ProjectFileReader found under a folder, and how much it had to skip, for the package
    // scanner's summary log line
    public sealed record ProjectFileReadResult(
        // Sorted by file path (ordinal), then line number
        IReadOnlyList<DeclaredPackage> Packages,
        // .csproj and Directory.Packages.props files found, including any that were not valid XML
        int ProjectFileCount,
        int InvalidFileCount,
        // Versions or package IDs written as an MSBuild property, such as $(JsonVersion)
        int PropertyVersionCount,
        // References with no version anywhere, which a real build rejects too
        int MissingVersionCount);
}
