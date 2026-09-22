using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Planner.Domain.Entities;

namespace Planner.Infrastructure.Configurations;

public class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.Property(p => p.Name).HasMaxLength(200).IsRequired();
        builder.Property(p => p.Summary).HasMaxLength(500);
        builder.Property(p => p.Color).HasMaxLength(9);
        builder.Property(p => p.Rank).IsRank();

        builder.HasOne(p => p.Team)
            .WithMany(t => t.Projects)
            .HasForeignKey(p => p.TeamId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(p => p.LeadUser)
            .WithMany()
            .HasForeignKey(p => p.LeadUserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(p => new { p.TeamId, p.Name }).IsUnique();
        builder.HasIndex(p => new { p.TeamId, p.Status });
        builder.HasIndex(p => new { p.TeamId, p.Rank });
        builder.HasIndex(p => p.TargetDate);

        builder.Property(x => x.UpdatedAt).IsConcurrencyToken();
    }
}

public class MilestoneConfiguration : IEntityTypeConfiguration<Milestone>
{
    public void Configure(EntityTypeBuilder<Milestone> builder)
    {
        builder.Property(m => m.Name).HasMaxLength(200).IsRequired();
        builder.Property(m => m.Description).HasMaxLength(4000);
        builder.Property(m => m.Rank).IsRank();

        builder.HasOne(m => m.Project)
            .WithMany(p => p.Milestones)
            .HasForeignKey(m => m.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(m => new { m.ProjectId, m.Name }).IsUnique();
        builder.HasIndex(m => new { m.ProjectId, m.Rank });

        builder.Property(x => x.UpdatedAt).IsConcurrencyToken();
    }
}

public class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    public void Configure(EntityTypeBuilder<Document> builder)
    {
        builder.Property(d => d.Title).HasMaxLength(300).IsRequired();
        builder.Property(d => d.Content).HasColumnType("text");

        builder.HasOne(d => d.Team)
            .WithMany()
            .HasForeignKey(d => d.TeamId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(d => d.Project)
            .WithMany(p => p.Documents)
            .HasForeignKey(d => d.ProjectId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(d => d.CreatedBy)
            .WithMany()
            .HasForeignKey(d => d.CreatedById)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(d => d.UpdatedBy)
            .WithMany()
            .HasForeignKey(d => d.UpdatedById)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(d => new { d.TeamId, d.Title });
        builder.HasIndex(d => d.ProjectId);
        builder.HasIndex(d => d.Title).HasMethod("gin").HasOperators("gin_trgm_ops");

        builder.Property(x => x.UpdatedAt).IsConcurrencyToken();
    }
}
