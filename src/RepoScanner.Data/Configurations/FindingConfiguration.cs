using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RepoScanner.Core.Entities;
using RepoScanner.Core.Enums;

namespace RepoScanner.Data.Configurations
{
    // Findings: one row per problem in a repository, updated in place by each scan. varchar is used
    // for values that are only ever ASCII (hashes, rule IDs); nvarchar for anything a person might type.
    internal sealed class FindingConfiguration : IEntityTypeConfiguration<Finding>
    {
        // ADR-019: a suppression must have a reason. Built with nameof so it follows any rename:
        // [Status] <> 'Suppressed' OR [SuppressionReason] IS NOT NULL
        private static readonly string SuppressionReasonRule =
            $"[{nameof(Finding.Status)}] <> '{nameof(FindingStatus.Suppressed)}' OR [{nameof(Finding.SuppressionReason)}] IS NOT NULL";

        public void Configure(EntityTypeBuilder<Finding> builder)
        {
            // A check constraint, so the database enforces the rule whichever application writes the row
            builder.ToTable("Findings", table => table.HasCheckConstraint("CK_Findings_SuppressionReason", SuppressionReasonRule));

            builder.HasKey(finding => finding.Id);

            // SHA-256 in hexadecimal: 64 characters
            builder.Property(finding => finding.Fingerprint).IsRequired().HasMaxLength(64).IsUnicode(false);
            builder.Property(finding => finding.ScannerType).IsStoredAsText();
            builder.Property(finding => finding.RuleId).IsRequired().HasMaxLength(10).IsUnicode(false);
            builder.Property(finding => finding.Severity).IsStoredAsText();
            builder.Property(finding => finding.FilePath).IsRequired().HasMaxLength(1024);
            builder.Property(finding => finding.Message).IsRequired().HasMaxLength(1000);

            // Secret findings only; the raw secret value is never stored
            builder.Property(finding => finding.MaskedPreview).HasMaxLength(200);
            builder.Property(finding => finding.SecretHash).HasMaxLength(64).IsUnicode(false);

            // Package findings only
            builder.Property(finding => finding.PackageName).HasMaxLength(256);
            builder.Property(finding => finding.PackageVersion).HasMaxLength(64);
            builder.Property(finding => finding.AdvisoryUrl).HasMaxLength(2048);

            builder.Property(finding => finding.Status).IsStoredAsText();
            builder.Property(finding => finding.SuppressedBy).HasMaxLength(256);
            builder.Property(finding => finding.SuppressionReason).HasMaxLength(1000);

            // Restrict: deleting a repository never deletes its findings (ADR-019)
            builder.HasOne(finding => finding.Repository)
                .WithMany(repository => repository.Findings)
                .HasForeignKey(finding => finding.RepositoryId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Restrict);

            // ADR-004: the queue delivers at least once, so a repeated message must never add the same finding twice
            builder.HasIndex(finding => new { finding.RepositoryId, finding.Fingerprint }).IsUnique();

            // A repository's open findings, for the reconciler and the dashboard
            builder.HasIndex(finding => new { finding.RepositoryId, finding.Status });
        }
    }
}
