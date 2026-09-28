using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NuGet.Versioning;
using RepoScanner.Core.Enums;
using RepoScanner.Core.Scanning;

namespace RepoScanner.Scanner.Packages
{
    // Finds NuGet packages with known vulnerabilities (PKG001) and deprecated versions (PKG002, #75) in the project
    // files under a folder (Issue 5 design, Package scanner and Deprecated packages): what the project files declare
    // (#43), checked against nuget.org's data (#44) at the version each really is (#45). Only packages listed
    // directly are checked (ADR-026).
    public sealed class PackageScanner : IScanner
    {
        public const string VulnerableRuleId = "PKG001";

        public const string DeprecatedRuleId = "PKG002";

        private readonly ProjectFileReader _reader;
        private readonly NuGetVulnerabilityIndexClient _client;
        private readonly ILogger<PackageScanner> _logger;

        public PackageScanner(ProjectFileReader reader, NuGetVulnerabilityIndexClient client, ILogger<PackageScanner> logger)
        {
            _reader = reader;
            _client = client;
            _logger = logger;
        }

        public ScannerType ScannerType => ScannerType.Package;

        // Throws NuGetUnavailableException when nuget.org cannot be reached; the scan run then carries on
        // without this scanner (#74)
        public async Task<IReadOnlyList<RawFinding>> ScanAsync(string rootPath, CancellationToken cancellationToken)
        {
            ProjectFileReadResult read = await _reader.ReadAsync(rootPath, cancellationToken);
            List<RawFinding> findings = new List<RawFinding>();
            int notOnNuGetCount = 0;
            int unresolvedCount = 0;

            // With nothing declared, no request is made (decision 19)
            foreach (DeclaredPackage package in read.Packages)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Resolved first: both rules need the version a build gets (decision 29)
                IReadOnlyList<NuGetVersion>? availableVersions = null;
                if (!VersionResolver.IsPlainVersion(package.VersionText))
                {
                    availableVersions = await _client.GetVersionsAsync(package.PackageId, cancellationToken);
                    if (availableVersions == null)
                    {
                        notOnNuGetCount++;
                        continue;
                    }
                }

                NuGetVersion? version = VersionResolver.Resolve(package.VersionText, availableVersions);
                if (version == null)
                {
                    unresolvedCount++;
                    continue;
                }

                // PKG001. nuget.org lists an advisory once per affected range, all with the same link: one finding
                // per link, as serious as the worst of its entries that the version is in (decision 14)
                IReadOnlyList<PackageAdvisory> advisories = await _client.GetAdvisoriesAsync(package.PackageId, cancellationToken);
                foreach (IGrouping<string, PackageAdvisory> advisory in advisories.GroupBy(entry => entry.AdvisoryUrl, StringComparer.Ordinal))
                {
                    List<PackageAdvisory> matching = advisory.Where(entry => entry.AffectedVersions.Satisfies(version)).ToList();
                    if (matching.Count > 0)
                    {
                        findings.Add(ToFinding(package, version, VulnerableRuleId, advisory.Key, HighestSeverity(package, matching), "has a known vulnerability"));
                    }
                }

                // PKG002 (#75): at most one per package, linked to the version's nuget.org page (decision 27)
                PackageDeprecation? deprecation = await _client.GetDeprecationAsync(package.PackageId, version, cancellationToken);
                if (deprecation != null)
                {
                    findings.Add(ToFinding(package, version, DeprecatedRuleId, deprecation.DetailsUrl, DeprecatedSeverity(package, deprecation), Deprecated(deprecation)));
                }
            }

            List<RawFinding> sorted = findings
                .OrderBy(finding => finding.FilePath, StringComparer.Ordinal)
                .ThenBy(finding => finding.LineNumber)
                .ThenBy(finding => finding.PackageName, StringComparer.Ordinal)
                .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
                .ThenBy(finding => finding.AdvisoryUrl, StringComparer.Ordinal)
                .ToList();

            _logger.LogInformation(
                "Package scan read {ProjectFileCount} project files and checked {PackageCount} packages: {FindingCount} findings; " +
                "skipped {InvalidFileCount} files that are not valid XML, {PropertyVersionCount} versions written as properties, " +
                "{MissingVersionCount} references with no version, {NotOnNuGetCount} packages nuget.org does not list and " +
                "{UnresolvedCount} versions that did not resolve",
                read.ProjectFileCount, read.Packages.Count, sorted.Count, read.InvalidFileCount, read.PropertyVersionCount,
                read.MissingVersionCount, notOnNuGetCount, unresolvedCount);

            return sorted;
        }

        private Severity HighestSeverity(DeclaredPackage package, IReadOnlyList<PackageAdvisory> matching)
        {
            foreach (PackageAdvisory entry in matching.Where(entry => !AdvisorySeverity.IsKnown(entry.Severity)))
            {
                _logger.LogWarning("Advisory {AdvisoryUrl} for {PackageId} has severity {SeverityValue}, which nuget.org does not document; reported as High",
                    entry.AdvisoryUrl, package.PackageId, entry.Severity);
            }

            return matching.Max(entry => AdvisorySeverity.ToSeverity(entry.Severity));
        }

        private Severity DeprecatedSeverity(DeclaredPackage package, PackageDeprecation deprecation)
        {
            foreach (string reason in deprecation.Reasons.Where(reason => !DeprecationSeverity.IsKnown(reason)))
            {
                _logger.LogWarning("{PackageId} is deprecated for {Reason}, a reason nuget.org does not document; reported as Low",
                    package.PackageId, reason);
            }

            return DeprecationSeverity.ToSeverity(deprecation.Reasons);
        }

        // "is deprecated (legacy); use Azure.Storage.Blobs instead", or without the alternative when nuget.org names
        // none (decision 28). The author's own note is left on the nuget.org page.
        private static string Deprecated(PackageDeprecation deprecation)
        {
            string reasons = deprecation.Reasons.Count == 0
                ? string.Empty
                : " (" + string.Join(", ", deprecation.Reasons.Select(InWords)) + ")";
            string alternative = deprecation.AlternatePackageId == null ? string.Empty : "; use " + deprecation.AlternatePackageId + " instead";

            return "is deprecated" + reasons + alternative;
        }

        // CriticalBugs as "critical bugs"; Legacy, Other and any other reason in lower case
        private static string InWords(string reason)
        {
            return string.Equals(reason, "CriticalBugs", StringComparison.OrdinalIgnoreCase) ? "critical bugs" : reason.ToLowerInvariant();
        }

        // problem is what is wrong with the version, such as "has a known vulnerability"
        private static RawFinding ToFinding(DeclaredPackage package, NuGetVersion version, string ruleId, string link, Severity severity, string problem)
        {
            string resolvedVersion = version.ToNormalizedString();

            // "(from 12.0.*)" when the version written was not the version checked (decision 17)
            string from = VersionResolver.IsPlainVersion(package.VersionText) ? string.Empty : " (from " + package.VersionText + ")";

            return new RawFinding
            {
                ScannerType = ScannerType.Package,
                RuleId = ruleId,
                Severity = severity,
                FilePath = package.FilePath,
                LineNumber = package.LineNumber,
                Message = package.PackageId + " " + resolvedVersion + from + " " + problem,
                PackageName = package.PackageId,
                PackageVersion = resolvedVersion,
                // The advisory for PKG001, the version's nuget.org page for PKG002 (ADR-024)
                AdvisoryUrl = link
            };
        }
    }
}
