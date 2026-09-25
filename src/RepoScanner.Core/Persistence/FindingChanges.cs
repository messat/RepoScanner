using System.Collections.Generic;
using RepoScanner.Core.Entities;

namespace RepoScanner.Core.Persistence
{
    // Everything one scan changes about a repository's findings, saved in one go by
    // IFindingRepository.SaveChangesAsync so it is all kept or none of it is
    public sealed record FindingChanges(
        // Findings seen for the first time; their Events (such as Detected) are saved with them
        IReadOnlyList<Finding> NewFindings,
        // Findings that already exist and have changed, for example LastSeenUtc or Status
        IReadOnlyList<Finding> UpdatedFindings,
        // Events for findings that already exist, such as Resolved or Reopened
        IReadOnlyList<FindingEvent> NewEvents);
}
