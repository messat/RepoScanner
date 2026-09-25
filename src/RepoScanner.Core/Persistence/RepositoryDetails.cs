using System;

namespace RepoScanner.Core.Persistence
{
    // The repository a scan is about, as the queue message describes it. StartRunAsync stores these
    // details, matching an existing repository on AzureDevOpsId so a rename keeps its history.
    public sealed record RepositoryDetails(
        Guid AzureDevOpsId,
        string ProjectName,
        string Name,
        string DefaultBranch);
}
