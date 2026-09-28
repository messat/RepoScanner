using System.Collections.Generic;

namespace RepoScanner.Scanner.Packages
{
    // Why nuget.org marks one version deprecated (Legacy, CriticalBugs, Other), the package it names instead, if
    // any, and the version's nuget.org page, which shows the author's notice (Issue 5 design, Deprecated packages)
    public sealed record PackageDeprecation(IReadOnlyList<string> Reasons, string? AlternatePackageId, string DetailsUrl);
}
