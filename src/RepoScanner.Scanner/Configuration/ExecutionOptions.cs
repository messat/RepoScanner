using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace RepoScanner.Scanner.Configuration
{
    // How long the queue loop may take, bound from the "Scanner:Execution" configuration section and validated
    // when the host starts (ADR-022). Queue mode only: --path has no time limits (Issue 7 design, Settings).
    public sealed record ExecutionOptions : IValidatableObject
    {
        public const string SectionName = "Scanner:Execution";

        // The budget for one execution. 50 stops cleanly before the job's replicaTimeout of 60 minutes: no
        // repository is started unless ScanTimeoutPerRepoMinutes still fits in what is left.
        [Range(1, 1440)]
        public int MaxRunDurationMinutes { get; init; } = 50;

        // One repository's scan is stopped, and its run marked Failed, after this long. About 2.5 times ADR-012's
        // estimate of 6 minutes. Plus 5 minutes, it is also when a Running scan run counts as abandoned (#33).
        [Range(1, 1440)]
        public int ScanTimeoutPerRepoMinutes { get; init; } = 15;

        // How long a taken message stays hidden from other runs: replicaTimeout plus 5 in Azure (ADR-013), so it
        // never reappears while the run that took it is alive. 1 in Development, so a retry can be watched locally.
        // Azure's limit is 7 days.
        [Range(1, 10080)]
        public int VisibilityTimeoutMinutes { get; init; } = 65;

        // Runs after the attribute checks above pass
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (ScanTimeoutPerRepoMinutes > MaxRunDurationMinutes)
            {
                yield return new ValidationResult(
                    "Scanner:Execution:ScanTimeoutPerRepoMinutes must be no more than MaxRunDurationMinutes, or no repository is ever started.",
                    new[] { nameof(ScanTimeoutPerRepoMinutes) });
            }
        }
    }
}
