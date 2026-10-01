using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using RepoScanner.Core.Enums;

namespace RepoScanner.Scanner.Configuration
{
    // Settings for the storage queue this execution reads from, bound from the
    // "Scanner:Queue" configuration section and validated when the host starts.
    public sealed record QueueOptions : IValidatableObject
    {
        public const string SectionName = "Scanner:Queue";

        // The two lanes (ADR-011): the nightly job reads one, the on-demand job the other
        public const string NightlyQueueName = "scan-nightly";
        public const string OnDemandQueueName = "scan-ondemand";

        // Azure Storage queue names: 3 to 63 characters, lowercase letters, numbers and hyphens
        private const string QueueNamePattern = "^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$";
        private const string QueueNameMessage = "{0} must be 3 to 63 characters of lowercase letters, numbers and hyphens.";

        // Set only when Scanner:Queue:PoisonQueueName is configured; otherwise the default below applies
        private readonly string? _poisonQueueName;

        // The queue name is configuration, not code (ADR-011). Only the two lanes are valid, because the
        // queue is what says whether a scan is nightly or on-demand (Issue 7 design, decision 5).
        [Required]
        [AllowedValues(NightlyQueueName, OnDemandQueueName, ErrorMessage = "{0} must be " + NightlyQueueName + " or " + OnDemandQueueName + ".")]
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

        // How long a poisoned copy is kept: long enough to be noticed, where Azure's default is 7 days
        // (Issue 7 design, decision 14). At most a year, well inside the longest expiry Azure accepts.
        [Range(1, 365)]
        public int PoisonMessageTimeToLiveDays { get; init; } = 30;

        // Which lane a scan from this queue belongs to, saved as ScanRun.TriggerType
        public ScanTrigger Trigger => Name == OnDemandQueueName ? ScanTrigger.OnDemand : ScanTrigger.Nightly;

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
