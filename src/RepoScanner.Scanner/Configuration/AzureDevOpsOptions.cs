using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace RepoScanner.Scanner.Configuration
{
    // Where queue mode clones from, bound from the "Scanner:AzureDevOps" configuration section and validated when the
    // host starts (ADR-022). Queue mode only: --path needs no Azure DevOps (Issue 8 design, Settings).
    public sealed record AzureDevOpsOptions : IValidatableObject
    {
        public const string SectionName = "Scanner:AzureDevOps";

        // https://dev.azure.com/<organisation>: one organisation per scanner deployment, since a PAT belongs to one
        // (decision 5). A file:/// folder of bare repositories in tests.
        public string OrganizationUrl { get; init; } = string.Empty;

        // A personal access token with Code (Read) only, shared with the enqueuer (decision 4). Locally the user
        // environment variable Scanner__AzureDevOps__Pat, in Azure a Key Vault secret reference; never an appsettings
        // file (ADR-007). [Required] also rejects an empty or whitespace-only value.
        [Required(ErrorMessage = "Scanner:AzureDevOps:Pat is required in queue mode. Locally, set the Scanner__AzureDevOps__Pat user environment variable to a personal access token with Code (Read).")]
        public string Pat { get; init; } = string.Empty;

        // Runs after the attribute checks above pass. The message never repeats the value, which could hold a password.
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            // https only, so the PAT is never sent in the clear; file for the tests' bare repositories. A user name in
            // the URL, as Azure DevOps' Clone dialog offers it, is the pattern this tool flags.
            bool valid = Uri.TryCreate(OrganizationUrl, UriKind.Absolute, out Uri? uri)
                && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeFile)
                && uri.UserInfo.Length == 0;

            if (!valid)
            {
                yield return new ValidationResult(
                    "Scanner:AzureDevOps:OrganizationUrl must be an https URL such as https://dev.azure.com/<organisation>, with no user name in it",
                    new[] { nameof(OrganizationUrl) });
            }
        }

        // A record's ToString prints every property, so a settings object in a log line would print the PAT
        private bool PrintMembers(StringBuilder builder)
        {
            builder.Append("OrganizationUrl = ").Append(OrganizationUrl).Append(", Pat = ***");
            return true;
        }
    }
}
