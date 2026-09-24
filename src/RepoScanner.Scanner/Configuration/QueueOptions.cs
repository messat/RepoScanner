using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace RepoScanner.Scanner.Configuration
{
    // Settings for the storage queue this execution reads from, bound from the
    // "Scanner:Queue" configuration section and validated when the host starts.
    public sealed record QueueOptions : IValidatableObject
    {
        public const string SectionName = "Scanner:Queue";

        // Azure Storage queue names: 3 to 63 characters, lowercase letters, numbers and hyphens
        private const string QueueNamePattern = "^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$";
        private const string QueueNameMessage = "{0} must be 3 to 63 characters of lowercase letters, numbers and hyphens.";

        // Set only when Scanner:Queue:PoisonQueueName is configured; otherwise the default below applies
        private readonly string? _poisonQueueName;

        // scan-nightly or scan-ondemand: the queue name is configuration, not code (ADR-011)
        [Required]
        [RegularExpression(QueueNamePattern, ErrorMessage = QueueNameMessage)]
        public string Name { get; init; } = string.Empty;

        // Local development only (Azurite): UseDevelopmentStorage=true
        public string? ConnectionString { get; init; }

        // Azure only, used with DefaultAzureCredential (managed identity, ADR-007)
        public Uri? ServiceUri { get; init; }

        // Where messages go after failing too many times. Each lane has its own (ADR-013), so the
        // default is the queue name plus "-poison": scan-nightly-poison, scan-ondemand-poison.
        // Empty while Name is missing, so that problem is reported once, against Name.
        [RegularExpression(QueueNamePattern, ErrorMessage = QueueNameMessage)]
        public string PoisonQueueName
        {
            get => _poisonQueueName ?? (string.IsNullOrEmpty(Name) ? string.Empty : Name + "-poison");
            init => _poisonQueueName = value;
        }

        // A message dequeued more times than this is moved to the poison queue (ADR-013)
        [Range(1, int.MaxValue)]
        public int MaxDequeueCount { get; init; } = 3;

        // Rules that involve more than one property. Runs after the attribute checks above pass.
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            bool hasConnectionString = !string.IsNullOrWhiteSpace(ConnectionString);
            bool hasServiceUri = ServiceUri is not null;

            if (hasConnectionString == hasServiceUri)
            {
                yield return new ValidationResult(
                    "Exactly one of Scanner:Queue:ConnectionString (local) or Scanner:Queue:ServiceUri (Azure) must be set.",
                    new[] { nameof(ConnectionString), nameof(ServiceUri) });
            }
        }
    }
}
