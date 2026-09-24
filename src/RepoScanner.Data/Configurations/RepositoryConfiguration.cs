using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RepoScanner.Core.Entities;

namespace RepoScanner.Data.Configurations
{
    // Repositories: one row per Azure DevOps repository. Its relationships are configured on the
    // other side, in ScanRunConfiguration and FindingConfiguration.
    internal sealed class RepositoryConfiguration : IEntityTypeConfiguration<Repository>
    {
        public void Configure(EntityTypeBuilder<Repository> builder)
        {
            builder.ToTable("Repositories");

            builder.HasKey(repository => repository.Id);

            builder.Property(repository => repository.ProjectName).IsRequired().HasMaxLength(256);
            builder.Property(repository => repository.Name).IsRequired().HasMaxLength(256);
            builder.Property(repository => repository.DefaultBranch).IsRequired().HasMaxLength(256);

            // The scanner finds a repository by its Azure DevOps ID, so that ID must identify one row
            builder.HasIndex(repository => repository.AzureDevOpsId).IsUnique();
        }
    }
}
