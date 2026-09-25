using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RepoScanner.Core.Enums;
using RepoScanner.Core.Scanning;
using RepoScanner.Scanner.Files;

namespace RepoScanner.Scanner.Secrets
{
    // Finds hard-coded secrets (SEC001 to SEC007) in the files under a folder. A secret's raw value
    // exists only inside AddFindings: it goes to the allow-list, the masker and the hasher and nowhere
    // else, so it never reaches a finding, a log line or the database (Issue 3 design).
    public sealed class SecretScanner : IScanner
    {
        private readonly RepositoryFileWalker _walker;
        private readonly IReadOnlyList<ISecretRule> _rules;
        private readonly ILogger<SecretScanner> _logger;

        public SecretScanner(RepositoryFileWalker walker, IEnumerable<ISecretRule> rules, ILogger<SecretScanner> logger)
        {
            _walker = walker;

            // Sorted by ID rather than trusting the registration order: the lower-numbered, more specific
            // rule must run first to win an overlap
            _rules = rules.OrderBy(rule => rule.RuleId, StringComparer.Ordinal).ToList();
            _logger = logger;
        }

        public ScannerType ScannerType => ScannerType.Secret;

        public async Task<IReadOnlyList<RawFinding>> ScanAsync(string rootPath, CancellationToken cancellationToken)
        {
            IReadOnlyList<RepositoryFile> files = _walker.GetFiles(rootPath);
            List<RawFinding> findings = new List<RawFinding>();
            int placeholderCount = 0;
            int unreadableCount = 0;

            foreach (RepositoryFile file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Null when the file cannot be read now (locked or deleted since the walk); the walker has
                // logged a warning naming it, and the rest of the folder is still scanned
                string? text = await _walker.TryReadTextAsync(file, cancellationToken);
                if (text == null)
                {
                    unreadableCount++;
                    continue;
                }

                placeholderCount += AddFindings(file.RelativePath, text, findings);
            }

            List<RawFinding> sorted = findings
                .OrderBy(finding => finding.FilePath, StringComparer.Ordinal)
                .ThenBy(finding => finding.LineNumber)
                .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
                .ToList();

            _logger.LogInformation(
                "Secret scan found {FindingCount} findings in {FileCount} files, ignored {PlaceholderCount} placeholders " +
                "and could not read {UnreadableCount} files",
                sorted.Count, files.Count - unreadableCount, placeholderCount, unreadableCount);

            return sorted;
        }

        // Runs every rule over one file's text and adds what they find. Returns how many placeholders
        // were ignored.
        private int AddFindings(string filePath, string text, List<RawFinding> findings)
        {
            // Where the secrets already reported in this file are, so a later rule cannot report one again
            List<Group> kept = new List<Group>();
            int placeholderCount = 0;

            foreach (ISecretRule rule in _rules)
            {
                foreach (Match match in MatchesOf(rule, filePath, text))
                {
                    Group secretGroup = match.Groups[SecretValue.GroupName];
                    string secret = SecretValue.FromMatch(match);

                    // The setting's name, for the rules that capture it (SEC003, SEC007)
                    Group nameGroup = match.Groups[SecretValue.NameGroupName];
                    string? settingName = nameGroup.Success ? nameGroup.Value : null;

                    if (PlaceholderAllowList.IsPlaceholder(secret, settingName))
                    {
                        placeholderCount++;
                        continue;
                    }

                    if (kept.Any(other => Overlaps(other, secretGroup)))
                    {
                        continue;
                    }

                    kept.Add(secretGroup);
                    findings.Add(new RawFinding
                    {
                        ScannerType = ScannerType.Secret,
                        RuleId = rule.RuleId,
                        Severity = rule.Severity,
                        FilePath = filePath,
                        LineNumber = LineNumberAt(text, match.Index),
                        Message = rule.Message,
                        MaskedPreview = SecretMasker.Mask(secret),
                        SecretHash = SecretHasher.Hash(secret)
                    });
                }
            }

            return placeholderCount;
        }

        // Every match of one rule in the text. A pattern that times out on this file is skipped for this
        // file only. The exception is not logged, because its Input property holds the file's text.
        private IReadOnlyList<Match> MatchesOf(ISecretRule rule, string filePath, string text)
        {
            try
            {
                // Matches are found lazily, so all of them are found here, inside the try
                return rule.Pattern.Matches(text).ToList();
            }
            catch (RegexMatchTimeoutException)
            {
                _logger.LogWarning("Rule {RuleId} timed out on {FilePath}; the other rules still ran on it", rule.RuleId, filePath);
                return Array.Empty<Match>();
            }
        }

        private static bool Overlaps(Group first, Group second)
        {
            return first.Index < second.Index + second.Length && second.Index < first.Index + first.Length;
        }

        // Counted from 1: one more than the number of line feeds before the match. Windows line endings
        // (\r\n) end in \n too, so they give the same number.
        private static int LineNumberAt(string text, int index)
        {
            return text.AsSpan(0, index).Count('\n') + 1;
        }
    }
}
