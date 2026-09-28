using NuGet.Versioning;

namespace RepoScanner.Scanner.Packages
{
    // One entry of nuget.org's vulnerability data for a package. Severity is nuget.org's number (0 low to
    // 3 critical), mapped onto Severity by AdvisorySeverity (#45). One advisory can have several entries,
    // one per affected range, all with the same link.
    public sealed record PackageAdvisory(int Severity, string AdvisoryUrl, VersionRange AffectedVersions);
}
