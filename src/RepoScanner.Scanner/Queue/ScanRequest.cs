using System;
using RepoScanner.Core.Persistence;

namespace RepoScanner.Scanner.Queue
{
    // One scan request from scan-nightly or scan-ondemand (Issue 7 design, Message format). The enqueuer
    // and the dashboard send it; ScanRequestReader reads it. Nightly or on-demand is not in it: that
    // comes from the queue it was read from (decision 5).
    public sealed record ScanRequest(Guid CorrelationId, Guid RepositoryId, string ProjectName, string RepositoryName, string DefaultBranch)
    {
        // The only format this scanner reads. Raised only for a change an older scanner cannot read
        // safely, such as a new required field; a retry never changes it (decision 4).
        public const int SchemaVersion = 1;

        // What StartRunAsync saves in the Repositories table, matched on the Azure DevOps ID
        public RepositoryDetails ToRepositoryDetails()
        {
            return new RepositoryDetails(RepositoryId, ProjectName, RepositoryName, DefaultBranch);
        }
    }
}
