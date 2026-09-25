using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RepoScanner.Core.Entities;

namespace RepoScanner.Data.Configurations
{
    // FindingEvents: a finding's history, only ever added to (ADR-019)
    internal sealed class FindingEventConfiguration : IEntityTypeConfiguration<FindingEvent>
    {
        public void Configure(EntityTypeBuilder<FindingEvent> builder)
        {
            builder.ToTable("FindingEvents");

            builder.HasKey(findingEvent => findingEvent.Id);

            builder.Property(findingEvent => findingEvent.EventType).IsStoredAsText();
            builder.Property(findingEvent => findingEvent.Actor).IsRequired().HasMaxLength(256);
            builder.Property(findingEvent => findingEvent.Detail).HasMaxLength(1000);

            builder.HasOne(findingEvent => findingEvent.Finding)
                .WithMany(finding => finding.Events)
                .HasForeignKey(findingEvent => findingEvent.FindingId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Restrict);

            // Optional: events from dashboard actions have no scan run. No collection on ScanRun, because
            // nothing needs to list a run's events. Restrict is also required by SQL Server here: FindingEvents
            // can be reached from Repositories through both Findings and ScanRuns, and SQL Server rejects two
            // cascade paths to one table.
            builder.HasOne(findingEvent => findingEvent.ScanRun)
                .WithMany()
                .HasForeignKey(findingEvent => findingEvent.ScanRunId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.Restrict);

            // A finding's history in order
            builder.HasIndex(findingEvent => new { findingEvent.FindingId, findingEvent.OccurredUtc });
        }
    }
}
