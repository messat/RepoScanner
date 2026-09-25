using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using RepoScanner.Core.Entities;
using RepoScanner.Core.Enums;
using RepoScanner.Data;
using RepoScanner.Scanner.Secrets;
using RepoScanner.Scanner.Tests.Secrets;

namespace RepoScanner.Scanner.Tests.Local
{
    // The whole program in local mode, with in-memory SQLite in place of SQL Server
    [TestFixture]
    public class LocalModeTests
    {
        // Satisfies the start-up check; SQL Server is swapped out, so nothing ever connects to it
        private static readonly string[] DatabaseArgs = { "--Scanner:Database:ConnectionString", "Server=unused;Database=unused" };

        private SqliteTestDatabase _database = null!;

        [SetUp]
        public void SetUp()
        {
            _database = new SqliteTestDatabase();
        }

        [TearDown]
        public void TearDown()
        {
            _database.Dispose();
        }

        // Issue 3 acceptance criterion 1: running against the fixture writes the expected findings to
        // SQL with masked previews
        [Test]
        public async Task Path_OnTheFixture_SavesTheLabelledSecretsWithMaskedPreviews()
        {
            (int exitCode, _) = await RunAsync("--path", FixtureSecrets.FolderPath);

            List<Finding> findings = SavedFindings();
            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(findings.Select(finding => (finding.RuleId, finding.FilePath, finding.LineNumber)),
                Is.EqualTo(FixtureSecrets.All.Select(secret => (secret.RuleId, secret.FilePath, (int?)secret.LineNumber))));

            for (int index = 0; index < findings.Count; index++)
            {
                FixtureSecret secret = FixtureSecrets.All[index];
                string label = secret.RuleId + " in " + secret.FilePath;

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(findings[index].MaskedPreview, Is.EqualTo(SecretMasker.Mask(secret.Value)), label);
                    Assert.That(findings[index].SecretHash, Is.EqualTo(SecretHasher.Hash(secret.Value)), label);
                    Assert.That(findings[index].Status, Is.EqualTo(FindingStatus.Open), label);
                    Assert.That(findings[index].Events.Single().EventType, Is.EqualTo(FindingEventType.Detected), label);
                }
            }
        }

        [Test]
        public async Task Path_OnTheFixture_CompletesTheRunWithOneSucceededSecretScannerResult()
        {
            await RunAsync("--path", FixtureSecrets.FolderPath);

            using (ScannerDbContext context = _database.CreateContext())
            {
                ScanRun run = context.ScanRuns.Include(saved => saved.ScannerResults).Single();
                ScannerResult result = run.ScannerResults.Single();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(run.Status, Is.EqualTo(ScanRunStatus.Completed));
                    Assert.That(run.NewCount, Is.EqualTo(FixtureSecrets.All.Count));
                    Assert.That(result.ScannerType, Is.EqualTo(ScannerType.Secret));
                    Assert.That(result.Status, Is.EqualTo(ScannerStatus.Succeeded));
                    Assert.That(result.FindingCount, Is.EqualTo(FixtureSecrets.All.Count));
                    Assert.That(context.Repositories.Single().ProjectName, Is.EqualTo("Local"));
                }
            }
        }

        // Design decision 2: the demo can be run as often as needed
        [Test]
        public async Task Path_OnTheFixtureTwice_AddsNothingTheSecondTime()
        {
            await RunAsync("--path", FixtureSecrets.FolderPath);
            (int exitCode, _) = await RunAsync("--path", FixtureSecrets.FolderPath);

            using (ScannerDbContext context = _database.CreateContext())
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(exitCode, Is.EqualTo(0));
                    Assert.That(context.Findings.Count(), Is.EqualTo(FixtureSecrets.All.Count));
                    Assert.That(context.ScanRuns.OrderBy(run => run.Id).Select(run => run.NewCount),
                        Is.EqualTo(new int[] { FixtureSecrets.All.Count, 0 }));
                }
            }
        }

        // Issue 3 acceptance criterion 2: no raw value in the saved rows or in the log
        [Test]
        public async Task Path_OnTheFixture_PutsNoRawValueInTheDatabaseOrTheLog()
        {
            (_, string output) = await RunAsync("--path", FixtureSecrets.FolderPath);

            string savedText;
            using (ScannerDbContext context = _database.CreateContext())
            {
                IEnumerable<string> findingRows = context.Findings.AsEnumerable().Select(finding =>
                    string.Join("|", finding.Fingerprint, finding.RuleId, finding.FilePath, finding.Message, finding.MaskedPreview, finding.SecretHash));
                IEnumerable<string> runRows = context.ScanRuns.AsEnumerable().Select(run => run.ErrorMessage ?? string.Empty);
                savedText = string.Join("\n", findingRows.Concat(runRows));
            }

            foreach (FixtureSecret secret in FixtureSecrets.All)
            {
                // Is.False with a message, not Does.Not.Contain: a failure must not print the value itself
                bool inDatabase = savedText.Contains(secret.Value, StringComparison.Ordinal);
                bool inLog = output.Contains(secret.Value, StringComparison.Ordinal);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(inDatabase, Is.False, secret.RuleId + " value from " + secret.FilePath + " leaked into the database");
                    Assert.That(inLog, Is.False, secret.RuleId + " value from " + secret.FilePath + " leaked into the log");
                }
            }
        }

        [Test]
        public async Task Path_ToAFolderThatDoesNotExist_ExitsTwoWithOneLine()
        {
            (int exitCode, string output) = await RunAsync("--path", Path.Combine(FixtureSecrets.FolderPath, "does-not-exist"));

            AssertExitTwoWithOneLine(exitCode, output);
        }

        [Test]
        public async Task Path_WithoutAFolder_ExitsTwoWithOneLine()
        {
            (int exitCode, string output) = await RunAsync("--path");

            AssertExitTwoWithOneLine(exitCode, output);
        }

        // Review focus: a file is not a folder
        [Test]
        public async Task Path_ToAFile_ExitsTwoWithOneLine()
        {
            (int exitCode, string output) = await RunAsync("--path", Path.Combine(FixtureSecrets.FolderPath, "README.md"));

            AssertExitTwoWithOneLine(exitCode, output);
        }

        private async Task<(int ExitCode, string Output)> RunAsync(params string[] args)
        {
            int exitCode = 0;
            string output = await ConsoleCapture.RunAsync(async () =>
            {
                exitCode = await Program.RunAsync(args.Concat(DatabaseArgs).ToArray(), services => _database.ReplaceDatabaseIn(services));
            });

            return (exitCode, output);
        }

        // Sorted in memory by ordinal, the order FixtureSecrets lists them in
        private List<Finding> SavedFindings()
        {
            using (ScannerDbContext context = _database.CreateContext())
            {
                return context.Findings.Include(finding => finding.Events).AsEnumerable()
                    .OrderBy(finding => finding.FilePath, StringComparer.Ordinal)
                    .ThenBy(finding => finding.LineNumber)
                    .ToList();
            }
        }

        private static void AssertExitTwoWithOneLine(int exitCode, string output)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(exitCode, Is.EqualTo(2));
                Assert.That(output.Split('\n').Count(line => line.Contains("FTL]", StringComparison.Ordinal)), Is.EqualTo(1));
                Assert.That(output, Does.Contain("--path"));
                Assert.That(output, Does.Not.Contain("   at "), "A settings problem should not print a stack trace");
            }
        }
    }
}
