using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Planner.Domain.Entities;

namespace Planner.Infrastructure.Configurations;

public class IssueConfiguration : IEntityTypeConfiguration<Issue>
{
    public void Configure(EntityTypeBuilder<Issue> builder)
    {
        builder.Property(i => i.Title).HasMaxLength(500).IsRequired();
        builder.Property(i => i.Description).HasColumnType("text");
        builder.Property(i => i.Rank).IsRank();

        builder.HasOne(i => i.Team)
            .WithMany(t => t.Issues)
            .HasForeignKey(i => i.TeamId)
            .OnDelete(DeleteBehavior.Cascade);

        // A state may not be dropped while issues still sit in it; the API forces a move first.
        builder.HasOne(i => i.State)
            .WithMany(s => s.Issues)
            .HasForeignKey(i => i.StateId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(i => i.Assignee)
            .WithMany()
            .HasForeignKey(i => i.AssigneeId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(i => i.Creator)
            .WithMany()
            .HasForeignKey(i => i.CreatorId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(i => i.Project)
            .WithMany(p => p.Issues)
            .HasForeignKey(i => i.ProjectId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(i => i.Milestone)
            .WithMany(m => m.Issues)
            .HasForeignKey(i => i.MilestoneId)
            .OnDelete(DeleteBehavior.SetNull);

        // Deleting a parent promotes its sub-issues to top level rather than destroying work.
        builder.HasOne(i => i.Parent)
            .WithMany(i => i.Children)
            .HasForeignKey(i => i.ParentId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(i => new { i.TeamId, i.Number }).IsUnique();
        builder.HasIndex(i => new { i.TeamId, i.StateId, i.Rank });
        builder.HasIndex(i => i.AssigneeId);
        builder.HasIndex(i => i.ProjectId);
        builder.HasIndex(i => i.MilestoneId);
        builder.HasIndex(i => i.ParentId);

        // Delta sync: clients ask for everything touched since their last successful poll.
        builder.HasIndex(i => i.UpdatedAt);

        builder.HasIndex(i => i.Title).HasMethod("gin").HasOperators("gin_trgm_ops");

        builder.Property(x => x.UpdatedAt).IsConcurrencyToken();
    }
}

public class IssueLabelConfiguration : IEntityTypeConfiguration<IssueLabel>
{
    public void Configure(EntityTypeBuilder<IssueLabel> builder)
    {
        builder.HasKey(x => new { x.IssueId, x.LabelId });

        builder.HasOne(x => x.Issue)
            .WithMany(i => i.Labels)
            .HasForeignKey(x => x.IssueId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Label)
            .WithMany(l => l.IssueLabels)
            .HasForeignKey(x => x.LabelId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => x.LabelId);
    }
}

public class IssueRelationConfiguration : IEntityTypeConfiguration<IssueRelation>
{
    public void Configure(EntityTypeBuilder<IssueRelation> builder)
    {
        builder.HasOne(r => r.SourceIssue)
            .WithMany(i => i.OutgoingRelations)
            .HasForeignKey(r => r.SourceIssueId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(r => r.TargetIssue)
            .WithMany(i => i.IncomingRelations)
            .HasForeignKey(r => r.TargetIssueId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(r => new { r.SourceIssueId, r.TargetIssueId, r.Type }).IsUnique();
        builder.HasIndex(r => r.TargetIssueId);
    }
}

public class CommentConfiguration : IEntityTypeConfiguration<Comment>
{
    public void Configure(EntityTypeBuilder<Comment> builder)
    {
        builder.Property(c => c.Body).HasColumnType("text").IsRequired();

        builder.HasOne(c => c.Issue)
            .WithMany(i => i.Comments)
            .HasForeignKey(c => c.IssueId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(c => c.Author)
            .WithMany()
            .HasForeignKey(c => c.AuthorId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(c => c.ParentComment)
            .WithMany(c => c.Replies)
            .HasForeignKey(c => c.ParentCommentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(c => new { c.IssueId, c.CreatedAt });

        builder.Property(x => x.UpdatedAt).IsConcurrencyToken();
    }
}

public class AttachmentConfiguration : IEntityTypeConfiguration<Attachment>
{
    public void Configure(EntityTypeBuilder<Attachment> builder)
    {
        builder.Property(a => a.FileName).HasMaxLength(300).IsRequired();
        builder.Property(a => a.ContentType).HasMaxLength(150);
        builder.Property(a => a.StorageUri).HasMaxLength(2000).IsRequired();

        builder.HasOne(a => a.Issue)
            .WithMany(i => i.Attachments)
            .HasForeignKey(a => a.IssueId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(a => a.UploadedBy)
            .WithMany()
            .HasForeignKey(a => a.UploadedById)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(a => a.IssueId);
    }
}

public class ActivityEventConfiguration : IEntityTypeConfiguration<ActivityEvent>
{
    public void Configure(EntityTypeBuilder<ActivityEvent> builder)
    {
        builder.Property(a => a.EntityType).HasMaxLength(40).IsRequired();
        builder.Property(a => a.Action).HasMaxLength(60).IsRequired();
        builder.Property(a => a.Data).HasColumnType("jsonb");

        builder.HasOne(a => a.Actor)
            .WithMany()
            .HasForeignKey(a => a.ActorId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(a => new { a.IssueId, a.CreatedAt });
        builder.HasIndex(a => new { a.TeamId, a.CreatedAt });
        builder.HasIndex(a => new { a.ProjectId, a.CreatedAt });
    }
}

public class AppUserConfiguration : IEntityTypeConfiguration<Planner.Domain.Identity.AppUser>
{
    public void Configure(EntityTypeBuilder<Planner.Domain.Identity.AppUser> builder)
    {
        builder.Property(u => u.DisplayName).HasMaxLength(150).IsRequired();
        builder.Property(u => u.AvatarUrl).HasMaxLength(2000);
        builder.Property(u => u.TimeZone).HasMaxLength(60);
        builder.HasIndex(u => u.DisplayName);
    }
}
