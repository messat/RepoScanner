using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RepoScanner.Core.Entities;

namespace RepoScanner.Data.Configurations
{
    // ScannerResults: how each scanner did within a scan run
    internal sealed class ScannerResultConfiguration : IEntityTypeConfiguration<ScannerResult>
    {
        public void Configure(EntityTypeBuilder<ScannerResult> builder)
        {
            builder.ToTable("ScannerResults");

            builder.HasKey(result => result.Id);

            builder.Property(result => result.ScannerType).IsStoredAsText();
            builder.Property(result => result.Status).IsStoredAsText();

            // No maximum length: nvarchar(max), because an exception message has no fixed size
            builder.Property(result => result.ErrorMessage);

            builder.HasOne(result => result.ScanRun)
                .WithMany(scanRun => scanRun.ScannerResults)
                .HasForeignKey(result => result.ScanRunId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Restrict);

            // Each scanner reports once per run
            builder.HasIndex(result => new { result.ScanRunId, result.ScannerType }).IsUnique();
        }
    }
}
