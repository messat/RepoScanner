using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RepoScanner.Core.Entities;

namespace RepoScanner.Data.Configurations
{
    // ScanRuns: one new row per scan, kept as history. Columns with a non-nullable type (int, Guid,
    // DateTime) are required; nullable ones (DateTime?, string?) are optional.
    internal sealed class ScanRunConfiguration : IEntityTypeConfiguration<ScanRun>
    {
        public void Configure(EntityTypeBuilder<ScanRun> builder)
        {
            builder.ToTable("ScanRuns");

            builder.HasKey(scanRun => scanRun.Id);

            builder.Property(scanRun => scanRun.TriggerType).IsStoredAsText();
            builder.Property(scanRun => scanRun.Status).IsStoredAsText();

            // A git commit SHA-1: 40 hexadecimal characters
            builder.Property(scanRun => scanRun.CommitSha).HasMaxLength(40).IsUnicode(false);

            // No maximum length: nvarchar(max), because an exception message has no fixed size
            builder.Property(scanRun => scanRun.ErrorMessage);

            // Restrict: deleting a repository never deletes its scan history (ADR-019)
            builder.HasOne(scanRun => scanRun.Repository)
                .WithMany(repository => repository.ScanRuns)
                .HasForeignKey(scanRun => scanRun.RepositoryId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Restrict);

            // "Is a scan already running for this repository?" (task #33)
            builder.HasIndex(scanRun => new { scanRun.RepositoryId, scanRun.Status });

            // The dashboard's latest scan per repository
            builder.HasIndex(scanRun => new { scanRun.RepositoryId, scanRun.StartedUtc });
        }
    }
}
