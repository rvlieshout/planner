using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Planner.Domain.Entities;

namespace Planner.Infrastructure.Configurations;

public class TeamConfiguration : IEntityTypeConfiguration<Team>
{
    public void Configure(EntityTypeBuilder<Team> builder)
    {
        builder.Property(t => t.Key).HasMaxLength(8).IsRequired();
        builder.Property(t => t.Name).HasMaxLength(120).IsRequired();
        builder.Property(t => t.Description).HasMaxLength(2000);
        builder.Property(t => t.Color).HasMaxLength(9);

        builder.HasIndex(t => t.Key).IsUnique();
        builder.HasIndex(t => t.ArchivedAt);

        // Optimistic concurrency: EF appends WHERE updated_at = @original to every UPDATE, so a
        // stale client write fails loudly instead of silently overwriting a change it never saw.
        builder.Property(x => x.UpdatedAt).IsConcurrencyToken();
    }
}

public class TeamMemberConfiguration : IEntityTypeConfiguration<TeamMember>
{
    public void Configure(EntityTypeBuilder<TeamMember> builder)
    {
        builder.HasKey(m => new { m.TeamId, m.UserId });

        builder.HasOne(m => m.Team)
            .WithMany(t => t.Members)
            .HasForeignKey(m => m.TeamId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(m => m.User)
            .WithMany(u => u.TeamMemberships)
            .HasForeignKey(m => m.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(m => m.UserId);
    }
}

public class WorkflowStateConfiguration : IEntityTypeConfiguration<WorkflowState>
{
    public void Configure(EntityTypeBuilder<WorkflowState> builder)
    {
        builder.Property(s => s.Name).HasMaxLength(60).IsRequired();
        builder.Property(s => s.Color).HasMaxLength(9);

        builder.HasOne(s => s.Team)
            .WithMany(t => t.WorkflowStates)
            .HasForeignKey(s => s.TeamId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(s => new { s.TeamId, s.Name }).IsUnique();
        builder.HasIndex(s => new { s.TeamId, s.Position });
    }
}

public class LabelConfiguration : IEntityTypeConfiguration<Label>
{
    public void Configure(EntityTypeBuilder<Label> builder)
    {
        builder.Property(l => l.Name).HasMaxLength(60).IsRequired();
        builder.Property(l => l.Color).HasMaxLength(9);
        builder.Property(l => l.Description).HasMaxLength(500);

        builder.HasOne(l => l.Team)
            .WithMany(t => t.Labels)
            .HasForeignKey(l => l.TeamId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(l => new { l.TeamId, l.Name }).IsUnique();

        // Postgres treats NULLs as distinct, so the composite index above would not stop two
        // organisation-wide labels sharing a name. This filtered index closes that hole.
        builder.HasIndex(l => l.Name)
            .IsUnique()
            .HasFilter("team_id IS NULL")
            .HasDatabaseName("ix_labels_org_name");
    }
}
