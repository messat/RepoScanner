using System.ComponentModel.DataAnnotations;

namespace RepoScanner.Scanner.Configuration
{
    // The scanner's database connection, bound from the "Scanner:Database" configuration section and
    // validated when the host starts (ADR-022). Locally the value comes from the user environment
    // variable Scanner__Database__ConnectionString, the same one dotnet ef reads. No appsettings file
    // holds it (ADR-007).
    public sealed record DatabaseOptions
    {
        public const string SectionName = "Scanner:Database";

        // The message names the setting in full, so the one log line says exactly what to set.
        // [Required] also rejects an empty or whitespace-only value.
        [Required(ErrorMessage = "Scanner:Database:ConnectionString is required. Locally, set the Scanner__Database__ConnectionString user environment variable (README: Database and migrations).")]
        public string ConnectionString { get; init; } = string.Empty;
    }
}
