using System;
using System.Security.Cryptography;
using System.Text;
using RepoScanner.Core.Enums;
using RepoScanner.Core.Scanning;

namespace RepoScanner.Scanner.Reconciliation
{
    // Identifies a finding across scans, so a scan can tell a problem it has seen before from a new one
    // (ADR-024). The line number is never part of it: a finding that moves down the file is the same
    // finding, and keeps its suppression and history.
    public static class FingerprintCalculator
    {
        // SHA-256 of the finding's identity as 64 lower-case hex characters, which fits the Fingerprint
        // column (varchar(64))
        public static string Calculate(RawFinding finding)
        {
            string identity = finding.ScannerType switch
            {
                // The formula Issue 3's interim code used, so findings already stored still match. One
                // finding per secret per file, however often it appears there (design decision 2).
                ScannerType.Secret => Join(finding.RuleId, finding.FilePath, Required(finding.SecretHash, nameof(RawFinding.SecretHash))),

                // NuGet package IDs ignore case, so the name is compared in lower case
                ScannerType.Package => Join(
                    finding.RuleId,
                    finding.FilePath,
                    Required(finding.PackageName, nameof(RawFinding.PackageName)).ToLowerInvariant(),
                    Required(finding.PackageVersion, nameof(RawFinding.PackageVersion)),
                    Required(finding.AdvisoryUrl, nameof(RawFinding.AdvisoryUrl))),

                ScannerType.CodePattern => Join(finding.RuleId, finding.FilePath, Required(finding.SnippetHash, nameof(RawFinding.SnippetHash))),

                _ => throw new ArgumentOutOfRangeException(nameof(finding), finding.ScannerType, "Unknown scanner type")
            };

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
            return Convert.ToHexStringLower(hash);
        }

        private static string Join(params string[] parts)
        {
            return string.Join("|", parts);
        }

        // A scanner that leaves out part of a finding's identity has a bug. Saving the finding anyway
        // could match it with a different finding later, so it is stopped here.
        private static string Required(string? value, string propertyName)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new ArgumentException($"A finding needs {propertyName} to be fingerprinted", propertyName);
            }

            return value;
        }
    }
}
