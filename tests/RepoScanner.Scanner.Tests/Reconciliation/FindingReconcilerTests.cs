using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RepoScanner.Core.Entities;
using RepoScanner.Core.Enums;
using RepoScanner.Core.Scanning;
using RepoScanner.Scanner.Reconciliation;

namespace RepoScanner.Scanner.Tests.Reconciliation
{
    // One test per reconciliation rule (Issue 4 design, Reconciliation rules), plus the cases around them
    [TestFixture]
    public class FindingReconcilerTests
    {
        private static readonly DateTime NowUtc = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

        private static readonly DateTime EarlierUtc = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

        // SHA-256 of "abc" and of "def", standing in for the hashes of two different secrets
        private const string HashOfAbc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

        private const string HashOfDef = "cb8379ac2098aa165029e3938a51da0bcecfc008fd6795f401178647f96c5b34";

        private static readonly ScanRun Run = new ScanRun { Id = 7, RepositoryId = 3 };

        // Gives each stored finding its own Id, as the database would
        private static int _nextId = 100;

        // Rule 1
        [Test]
        public void Reconcile_FoundAndStoredAsOpen_UpdatesLastSeenAndLineWithNoEvent()
        {
            Finding stored = Stored(Raw(lineNumber: 3), FindingStatus.Open);

            ReconciliationResult result = Reconcile(new[] { stored }, Raw(lineNumber: 5));

            Assert.That(stored.Status, Is.EqualTo(FindingStatus.Open));
            Assert.That(stored.LastSeenUtc, Is.EqualTo(NowUtc));
            Assert.That(stored.LineNumber, Is.EqualTo(5));
            Assert.That(stored.FirstSeenUtc, Is.EqualTo(EarlierUtc));
            Assert.That(result.Changes.NewFindings, Is.Empty);
            Assert.That(result.Changes.NewEvents, Is.Empty);

            // The same instance, so EF Core, which is tracking it, writes only the columns that changed
            Assert.That(result.Changes.UpdatedFindings.Single(), Is.SameAs(stored));
        }

        // Rule 2
        [Test]
        public void Reconcile_FoundAndNotStored_AddsAnOpenFindingWithADetectedEvent()
        {
            ReconciliationResult result = Reconcile(Array.Empty<Finding>(), Raw(lineNumber: 3));

            Finding added = result.Changes.NewFindings.Single();
            Assert.That(added.RepositoryId, Is.EqualTo(3));

            // The fingerprint of SEC003, src/Api/appsettings.json and HashOfAbc (FingerprintCalculatorTests)
            Assert.That(added.Fingerprint, Is.EqualTo("e1575900020f5028f7544bdfe5eb61e9b78452ace03801298dbf6475e5907a4d"));
            Assert.That(added.ScannerType, Is.EqualTo(ScannerType.Secret));
            Assert.That(added.RuleId, Is.EqualTo("SEC003"));
            Assert.That(added.Severity, Is.EqualTo(Severity.High));
            Assert.That(added.FilePath, Is.EqualTo("src/Api/appsettings.json"));
            Assert.That(added.LineNumber, Is.EqualTo(3));
            Assert.That(added.Message, Is.EqualTo("Password in a connection string"));
            Assert.That(added.MaskedPreview, Is.EqualTo("Pa****"));
            Assert.That(added.SecretHash, Is.EqualTo(HashOfAbc));
            Assert.That(added.Status, Is.EqualTo(FindingStatus.Open));
            Assert.That(added.FirstSeenUtc, Is.EqualTo(NowUtc));
            Assert.That(added.LastSeenUtc, Is.EqualTo(NowUtc));
            AssertScannerEvent(added.Events.Single(), FindingEventType.Detected);

            // A new finding's event is saved with it, not separately
            Assert.That(result.Changes.NewEvents, Is.Empty);
            Assert.That(result.NewCount, Is.EqualTo(1));
        }

        // A package finding keeps the columns only package findings have
        [Test]
        public void Reconcile_FoundAndNotStored_CopiesThePackageColumns()
        {
            Finding added = Reconcile(Array.Empty<Finding>(), PackageRaw()).Changes.NewFindings.Single();

            Assert.That(added.PackageName, Is.EqualTo("Newtonsoft.Json"));
            Assert.That(added.PackageVersion, Is.EqualTo("12.0.1"));
            Assert.That(added.AdvisoryUrl, Is.EqualTo("https://github.com/advisories/GHSA-5crp-9r3c-p9vr"));
        }

        // Rule 3
        [Test]
        public void Reconcile_NotFoundAndStoredAsOpen_ResolvesItWithAResolvedEvent()
        {
            Finding stored = Stored(Raw(lineNumber: 3), FindingStatus.Open);

            ReconciliationResult result = Reconcile(new[] { stored });

            Assert.That(stored.Status, Is.EqualTo(FindingStatus.Resolved));
            Assert.That(stored.ResolvedUtc, Is.EqualTo(NowUtc));
            Assert.That(stored.LastSeenUtc, Is.EqualTo(EarlierUtc));
            Assert.That(result.Changes.UpdatedFindings.Single(), Is.SameAs(stored));
            AssertScannerEvent(result.Changes.NewEvents.Single(), FindingEventType.Resolved, stored.Id);
            Assert.That(result.ResolvedCount, Is.EqualTo(1));
        }

        // Rule 3 applies whatever the status: code that has gone is fixed, even if it was suppressed
        // (design decision 4)
        [Test]
        public void Reconcile_NotFoundAndStoredAsSuppressed_ResolvesIt()
        {
            Finding stored = Suppressed(Stored(Raw(lineNumber: 3), FindingStatus.Suppressed), NowUtc.AddDays(7));

            ReconciliationResult result = Reconcile(new[] { stored });

            Assert.That(stored.Status, Is.EqualTo(FindingStatus.Resolved));
            AssertScannerEvent(result.Changes.NewEvents.Single(), FindingEventType.Resolved, stored.Id);
            Assert.That(result.ResolvedCount, Is.EqualTo(1));
        }

        // Rule 4. Covers a finding resolved in the dashboard too: its row is the same (design decision 12)
        [Test]
        public void Reconcile_FoundAndStoredAsResolved_ReopensItWithAReopenedEvent()
        {
            Finding stored = Stored(Raw(lineNumber: 3), FindingStatus.Resolved);
            stored.ResolvedUtc = EarlierUtc;

            ReconciliationResult result = Reconcile(new[] { stored }, Raw(lineNumber: 5));

            Assert.That(stored.Status, Is.EqualTo(FindingStatus.Open));
            Assert.That(stored.ResolvedUtc, Is.Null);
            Assert.That(stored.LastSeenUtc, Is.EqualTo(NowUtc));
            Assert.That(stored.LineNumber, Is.EqualTo(5));
            Assert.That(result.Changes.UpdatedFindings.Single(), Is.SameAs(stored));
            AssertScannerEvent(result.Changes.NewEvents.Single(), FindingEventType.Reopened, stored.Id);
            Assert.That(result.ReopenedCount, Is.EqualTo(1));
        }

        // Rule 5, for a suppression with a date still to come and for a permanent one (no date, ADR-019)
        [TestCase(7, TestName = "Reconcile_FoundAndSuppressedUntilLater_StaysSuppressedWithNoEvent")]
        [TestCase(null, TestName = "Reconcile_FoundAndSuppressedPermanently_StaysSuppressedWithNoEvent")]
        public void Reconcile_FoundAndSuppressionNotExpired_StaysSuppressedWithNoEvent(int? daysUntilExpiry)
        {
            DateTime? suppressedUntilUtc = daysUntilExpiry.HasValue ? NowUtc.AddDays(daysUntilExpiry.Value) : null;
            Finding stored = Suppressed(Stored(Raw(lineNumber: 3), FindingStatus.Suppressed), suppressedUntilUtc);

            ReconciliationResult result = Reconcile(new[] { stored }, Raw(lineNumber: 5));

            Assert.That(stored.Status, Is.EqualTo(FindingStatus.Suppressed));
            Assert.That(stored.LastSeenUtc, Is.EqualTo(NowUtc));
            Assert.That(stored.LineNumber, Is.EqualTo(5));
            Assert.That(result.Changes.NewEvents, Is.Empty);
            Assert.That(result.SuppressionExpiredCount, Is.EqualTo(0));
        }

        // Rule 6
        [Test]
        public void Reconcile_FoundAfterTheSuppressionDate_ReturnsItToOpenWithASuppressionExpiredEvent()
        {
            Finding stored = Suppressed(Stored(Raw(lineNumber: 3), FindingStatus.Suppressed), EarlierUtc);

            ReconciliationResult result = Reconcile(new[] { stored }, Raw(lineNumber: 5));

            Assert.That(stored.Status, Is.EqualTo(FindingStatus.Open));
            Assert.That(stored.LastSeenUtc, Is.EqualTo(NowUtc));
            Assert.That(stored.LineNumber, Is.EqualTo(5));
            AssertScannerEvent(result.Changes.NewEvents.Single(), FindingEventType.SuppressionExpired, stored.Id);
            Assert.That(result.SuppressionExpiredCount, Is.EqualTo(1));
        }

        // "At or before now": a suppression ending at this very moment has expired
        [Test]
        public void Reconcile_FoundOnTheSuppressionDate_ReturnsItToOpen()
        {
            Finding stored = Suppressed(Stored(Raw(lineNumber: 3), FindingStatus.Suppressed), NowUtc);

            Reconcile(new[] { stored }, Raw(lineNumber: 3));

            Assert.That(stored.Status, Is.EqualTo(FindingStatus.Open));
        }

        // A finding already resolved and still gone has nothing to change
        [Test]
        public void Reconcile_NotFoundAndStoredAsResolved_LeavesItAlone()
        {
            Finding stored = Stored(Raw(lineNumber: 3), FindingStatus.Resolved);
            stored.ResolvedUtc = EarlierUtc;

            ReconciliationResult result = Reconcile(new[] { stored });

            Assert.That(stored.Status, Is.EqualTo(FindingStatus.Resolved));
            Assert.That(stored.ResolvedUtc, Is.EqualTo(EarlierUtc));
            Assert.That(result.Changes.UpdatedFindings, Is.Empty);
            Assert.That(result.Changes.NewEvents, Is.Empty);
        }

        // The same secret twice in one file is one finding (design decision 2). Scanners sort by line, so
        // the first is the one nearest the top of the file.
        [Test]
        public void Reconcile_TheSameFingerprintTwiceInOneRun_AddsOneFindingAtTheFirstLine()
        {
            ReconciliationResult result = Reconcile(Array.Empty<Finding>(), Raw(lineNumber: 3), Raw(lineNumber: 9));

            Assert.That(result.Changes.NewFindings.Single().LineNumber, Is.EqualTo(3));
            Assert.That(result.NewCount, Is.EqualTo(1));
        }

        // A finding is matched by its fingerprint, so a different secret in the same file is a different finding
        [Test]
        public void Reconcile_ADifferentSecretInTheSameFile_IsNewAndTheOldOneIsResolved()
        {
            Finding stored = Stored(Raw(lineNumber: 3), FindingStatus.Open);

            ReconciliationResult result = Reconcile(new[] { stored }, Raw(lineNumber: 3) with { SecretHash = HashOfDef });

            Assert.That(result.Changes.NewFindings.Single().SecretHash, Is.EqualTo(HashOfDef));
            Assert.That(stored.Status, Is.EqualTo(FindingStatus.Resolved));
        }

        // Each count covers only its own kind of change
        [Test]
        public void Reconcile_WithOneOfEachChange_CountsEachOnce()
        {
            Finding stillOpen = Stored(Raw(lineNumber: 1, filePath: "a.json"), FindingStatus.Open);
            Finding reopened = Stored(Raw(lineNumber: 1, filePath: "b.json"), FindingStatus.Resolved);
            Finding expired = Suppressed(Stored(Raw(lineNumber: 1, filePath: "c.json"), FindingStatus.Suppressed), EarlierUtc);
            Finding resolved = Stored(Raw(lineNumber: 1, filePath: "d.json"), FindingStatus.Open);
            Finding[] stored = { stillOpen, reopened, expired, resolved };

            ReconciliationResult result = Reconcile(
                stored,
                Raw(lineNumber: 1, filePath: "a.json"),
                Raw(lineNumber: 1, filePath: "b.json"),
                Raw(lineNumber: 1, filePath: "c.json"),
                Raw(lineNumber: 1, filePath: "e.json"));

            Assert.That(result.NewCount, Is.EqualTo(1));
            Assert.That(result.ReopenedCount, Is.EqualTo(1));
            Assert.That(result.SuppressionExpiredCount, Is.EqualTo(1));
            Assert.That(result.ResolvedCount, Is.EqualTo(1));
            Assert.That(result.Changes.UpdatedFindings, Is.EquivalentTo(stored));
        }

        // The dashboard sets the suppression columns and the scanner never changes them (design decision 11)
        [TestCaseSource(nameof(EachRuleOnASuppressedRow))]
        public void Reconcile_NeverChangesTheSuppressionColumns(FindingStatus status, DateTime suppressedUntilUtc, bool found)
        {
            Finding stored = Suppressed(Stored(Raw(lineNumber: 3), status), suppressedUntilUtc);
            RawFinding[] foundThisRun = found ? new[] { Raw(lineNumber: 3) } : Array.Empty<RawFinding>();

            Reconcile(new[] { stored }, foundThisRun);

            Assert.That(stored.SuppressedUntilUtc, Is.EqualTo(suppressedUntilUtc));
            Assert.That(stored.SuppressedBy, Is.EqualTo("dev@example.com"));
            Assert.That(stored.SuppressionReason, Is.EqualTo("Test value in a sample file"));
        }

        private static IEnumerable<TestCaseData> EachRuleOnASuppressedRow()
        {
            yield return new TestCaseData(FindingStatus.Open, NowUtc.AddDays(7), true).SetArgDisplayNames("Rule 1");
            yield return new TestCaseData(FindingStatus.Open, NowUtc.AddDays(7), false).SetArgDisplayNames("Rule 3");
            yield return new TestCaseData(FindingStatus.Resolved, NowUtc.AddDays(7), true).SetArgDisplayNames("Rule 4");
            yield return new TestCaseData(FindingStatus.Suppressed, NowUtc.AddDays(7), true).SetArgDisplayNames("Rule 5");
            yield return new TestCaseData(FindingStatus.Suppressed, EarlierUtc, true).SetArgDisplayNames("Rule 6");
        }

        // Rule 7: a scanner that failed resolves nothing, while one that completed still resolves its own
        // missing findings (Issue 4 success criterion 4, ADR-025)
        [Test]
        public void Reconcile_NotFoundAndItsScannerDidNotComplete_LeavesItAsItWas()
        {
            Finding secret = Stored(Raw(lineNumber: 3), FindingStatus.Open);
            Finding package = Stored(PackageRaw(), FindingStatus.Open);
            Finding suppressedPackage = Suppressed(Stored(PackageRaw() with { PackageName = "Humanizer.Core" }, FindingStatus.Suppressed), NowUtc.AddDays(7));

            ReconciliationResult result = FindingReconciler.Reconcile(
                new[] { secret, package, suppressedPackage },
                Array.Empty<RawFinding>(),
                new HashSet<ScannerType> { ScannerType.Secret },
                Run,
                NowUtc);

            Assert.That(secret.Status, Is.EqualTo(FindingStatus.Resolved));
            Assert.That(package.Status, Is.EqualTo(FindingStatus.Open));
            Assert.That(package.ResolvedUtc, Is.Null);
            Assert.That(suppressedPackage.Status, Is.EqualTo(FindingStatus.Suppressed));
            Assert.That(result.Changes.UpdatedFindings, Is.EquivalentTo(new[] { secret }));
            Assert.That(result.Changes.NewEvents.Select(findingEvent => findingEvent.FindingId), Is.EquivalentTo(new[] { secret.Id }));
            Assert.That(result.ResolvedCount, Is.EqualTo(1));
        }

        // A scanner that did not run at all, such as the package scanner in local mode, is not in the set
        // either, so its findings are never resolved by a scan that could not have seen them
        [Test]
        public void Reconcile_WhenNoScannerCompleted_ResolvesNothing()
        {
            Finding stored = Stored(Raw(lineNumber: 3), FindingStatus.Open);

            ReconciliationResult result = FindingReconciler.Reconcile(new[] { stored }, Array.Empty<RawFinding>(), new HashSet<ScannerType>(), Run, NowUtc);

            Assert.That(stored.Status, Is.EqualTo(FindingStatus.Open));
            Assert.That(result.Changes.UpdatedFindings, Is.Empty);
            Assert.That(result.Changes.NewEvents, Is.Empty);
            Assert.That(result.ResolvedCount, Is.EqualTo(0));
        }

        // Rules 1 to 6, with every scanner type having completed
        private static ReconciliationResult Reconcile(IReadOnlyList<Finding> stored, params RawFinding[] found)
        {
            HashSet<ScannerType> allScannerTypes = new HashSet<ScannerType>(Enum.GetValues<ScannerType>());
            return FindingReconciler.Reconcile(stored, found, allScannerTypes, Run, NowUtc);
        }

        private static RawFinding PackageRaw()
        {
            return new RawFinding
            {
                ScannerType = ScannerType.Package,
                RuleId = "PKG001",
                Severity = Severity.High,
                FilePath = "src/Api/Api.csproj",
                Message = "Vulnerable package",
                PackageName = "Newtonsoft.Json",
                PackageVersion = "12.0.1",
                AdvisoryUrl = "https://github.com/advisories/GHSA-5crp-9r3c-p9vr"
            };
        }

        // Every event the scanner writes names the run, the time and the scanner as the actor
        private static void AssertScannerEvent(FindingEvent findingEvent, FindingEventType eventType, int? findingId = null)
        {
            Assert.That(findingEvent.EventType, Is.EqualTo(eventType));
            Assert.That(findingEvent.ScanRunId, Is.EqualTo(Run.Id));
            Assert.That(findingEvent.OccurredUtc, Is.EqualTo(NowUtc));
            Assert.That(findingEvent.Actor, Is.EqualTo("scanner"));

            if (findingId.HasValue)
            {
                Assert.That(findingEvent.FindingId, Is.EqualTo(findingId.Value));
            }
        }

        private static RawFinding Raw(int lineNumber, string filePath = "src/Api/appsettings.json")
        {
            return new RawFinding
            {
                ScannerType = ScannerType.Secret,
                RuleId = "SEC003",
                Severity = Severity.High,
                FilePath = filePath,
                LineNumber = lineNumber,
                Message = "Password in a connection string",
                MaskedPreview = "Pa****",
                SecretHash = HashOfAbc
            };
        }

        // A finding as GetByRepositoryAsync would return it, first and last seen a week ago
        private static Finding Stored(RawFinding raw, FindingStatus status)
        {
            return new Finding
            {
                Id = _nextId++,
                RepositoryId = Run.RepositoryId,
                Fingerprint = FingerprintCalculator.Calculate(raw),
                ScannerType = raw.ScannerType,
                RuleId = raw.RuleId,
                Severity = raw.Severity,
                FilePath = raw.FilePath,
                LineNumber = raw.LineNumber,
                Message = raw.Message,
                MaskedPreview = raw.MaskedPreview,
                SecretHash = raw.SecretHash,
                Status = status,
                FirstSeenUtc = EarlierUtc,
                LastSeenUtc = EarlierUtc
            };
        }

        // The columns the dashboard fills in when a developer suppresses a finding (ADR-019)
        private static Finding Suppressed(Finding finding, DateTime? suppressedUntilUtc)
        {
            finding.SuppressedUntilUtc = suppressedUntilUtc;
            finding.SuppressedBy = "dev@example.com";
            finding.SuppressionReason = "Test value in a sample file";
            return finding;
        }
    }
}
