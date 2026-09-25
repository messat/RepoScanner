namespace RepoScanner.Scanner.Files
{
    // One file to scan: where it is on disk, and its path within the scanned folder with / between
    // folders, which is what a finding records (Finding.FilePath)
    public sealed record RepositoryFile(string FullPath, string RelativePath);
}
