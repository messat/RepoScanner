using System;
using System.Collections.Generic;

namespace RepoScanner.Core.Entities
{
    // One Azure DevOps repository. The scanner adds it the first time the repository is scanned
    // and updates it when its name or default branch changes; the dashboard lists repositories from it.
    // The database mapping is in RepoScanner.Data, so Core stays free of EF Core.
    public sealed class Repository
    {
        // Primary key (mapped in Data/Configurations/RepositoryConfiguration.cs)
        public int Id { get; set; }

        // Azure DevOps' own ID for the repository; the scanner matches on it, not on the name,
        // so a renamed repository keeps its history
        public Guid AzureDevOpsId { get; set; }

        public string ProjectName { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        // As Azure DevOps returns it, for example refs/heads/main
        public string DefaultBranch { get; set; } = string.Empty;

        // Every scan of this repository, matched on ScanRun.RepositoryId; runs are kept, never overwritten
        public ICollection<ScanRun> ScanRuns { get; set; } = new List<ScanRun>();

        // Every finding in this repository, matched on Finding.RepositoryId
        public ICollection<Finding> Findings { get; set; } = new List<Finding>();
    }
}
