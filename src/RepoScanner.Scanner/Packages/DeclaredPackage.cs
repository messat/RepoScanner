namespace RepoScanner.Scanner.Packages
{
    // One package version as the project files declare it, at the file and line where the version is set:
    // the .csproj, or the <PackageVersion> line in Directory.Packages.props (Issue 5 design, decision 5).
    // VersionText is as written, such as 12.0.1 or 9.0.2.*; the scanner resolves it (#45).
    public sealed record DeclaredPackage(string FilePath, int LineNumber, string PackageId, string VersionText);
}
