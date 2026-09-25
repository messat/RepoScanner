using System;
using RepoScanner.Core.Entities;

namespace RepoScanner.Data.Tests
{
    // Valid entities with only the required columns set, so each test sets just what it is about
    internal static class TestEntities
    {
        public static readonly DateTime StartedUtc = new DateTime(2026, 9, 24, 2, 0, 0, DateTimeKind.Utc);

        // A new Azure DevOps ID each time, so two repositories never collide on the unique index by accident
        public static Repository NewRepository()
        {
            return new Repository
            {
                AzureDevOpsId = Guid.NewGuid(),
                ProjectName = "Lumina",
                Name = "RepoScanner",
                DefaultBranch = "refs/heads/main"
            };
        }

        public static ScanRun NewScanRun()
        {
            return new ScanRun
            {
                CorrelationId = Guid.NewGuid(),
                StartedUtc = StartedUtc
            };
        }

        public static Finding NewFinding(string fingerprint)
        {
            return new Finding
            {
                Fingerprint = fingerprint,
                RuleId = "SEC001",
                FilePath = "src/appsettings.json",
                Message = "Connection string with a password",
                FirstSeenUtc = StartedUtc,
                LastSeenUtc = StartedUtc
            };
        }
    }
}
