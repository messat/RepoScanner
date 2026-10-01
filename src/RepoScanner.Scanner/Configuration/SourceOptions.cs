using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;

namespace RepoScanner.Scanner.Configuration
{
    // Where queue mode finds a repository's code in Issue 7, bound from the "Scanner:Source" configuration section
    // and validated when the host starts (ADR-022). Queue mode only. For the Issue 7 demo: removed once Issue 8
    // clones instead.
    public sealed record SourceOptions : IValidatableObject
    {
        public const string SectionName = "Scanner:Source";

        // The folder the repositories are in: a message for RepoScanner scans <LocalRoot>\RepoScanner. The path
        // differs per machine, so it is a user environment variable, not an appsettings file.
        [Required(ErrorMessage = "Scanner:Source:LocalRoot is required in queue mode. Locally, set the Scanner__Source__LocalRoot user environment variable to the folder your repositories are in.")]
        public string LocalRoot { get; init; } = string.Empty;

        // Runs after the attribute checks above pass
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (!Directory.Exists(LocalRoot))
            {
                yield return new ValidationResult(
                    "Scanner:Source:LocalRoot must be a folder that exists: " + LocalRoot,
                    new[] { nameof(LocalRoot) });
            }
        }
    }
}
