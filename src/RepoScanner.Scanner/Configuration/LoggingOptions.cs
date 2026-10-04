using System.ComponentModel.DataAnnotations;

namespace RepoScanner.Scanner.Configuration
{
    // How the console writes log lines (#65, ADR-028): read from the Scanner:Logging section and checked when the host
    // starts, like every settings group, so a wrong value is one line and exit 2
    public sealed class LoggingOptions
    {
        public const string SectionName = "Scanner:Logging";

        // Text or Json, in any case. Not set: text in Development, JSON everywhere else (ConsoleLogFormat).
        [RegularExpression("^(?i:text|json)$", ErrorMessage = "Scanner:Logging:Format must be Text or Json")]
        public string? Format { get; init; }
    }
}
