using System.Collections.Generic;
using NuGet.Versioning;

namespace RepoScanner.Scanner.Packages
{
    // The version a declared package really is (Issue 5 design, Versions and severity): a plain version as
    // written, or, for a floating version or a range, the one restore would pick from what nuget.org has
    public static class VersionResolver
    {
        // True when the text is one plain version, such as 13.0.3, so no version list is needed. Restore uses
        // exactly that version when it exists (decision 8).
        public static bool IsPlainVersion(string versionText)
        {
            return NuGetVersion.TryParse(versionText, out _);
        }

        // The version to check, or null when the text is not a version or range, or no available version
        // matches. availableVersions is every version nuget.org lists, or null when it does not know the package.
        public static NuGetVersion? Resolve(string versionText, IReadOnlyList<NuGetVersion>? availableVersions)
        {
            if (NuGetVersion.TryParse(versionText, out NuGetVersion? plain))
            {
                return plain;
            }

            if (availableVersions == null || !VersionRange.TryParse(versionText, allowFloating: true, out VersionRange? range))
            {
                return null;
            }

            // NuGet's own choice, the one restore makes: the newest version a floating version allows, or the
            // lowest version in a range (decision 7)
            return range.FindBestMatch(availableVersions);
        }
    }
}
