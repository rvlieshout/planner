using Planner.Domain.Common;
using Planner.Contracts.Enums;

namespace Planner.Domain.Entities;

/// <summary>A body of work with a target date, owned by one team and made of milestones and issues.</summary>
public class Project : Entity, IArchivable
{
    public Guid TeamId { get; set; }
    public Team Team { get; set; } = null!;

    public string Name { get; set; } = string.Empty;

    /// <summary>One-line summary shown in list views.</summary>
    public string? Summary { get; set; }

    /// <summary>Long-form markdown brief. Richer, versionless notes belong in <see cref="Document"/>.</summary>
    public string? Description { get; set; }

    public ProjectStatus Status { get; set; } = ProjectStatus.Backlog;
    public ProjectHealth Health { get; set; } = ProjectHealth.OnTrack;
    public string Color { get; set; } = "#6E79F1";

    public Guid? LeadUserId { get; set; }
    public Identity.AppUser? LeadUser { get; set; }

    public DateOnly? StartDate { get; set; }
    public DateOnly? TargetDate { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }

    /// <summary>Position among the team's projects, as a <see cref="Common.Rank"/> key.</summary>
    public required string Rank { get; set; }

    public ICollection<Milestone> Milestones { get; set; } = [];
    public ICollection<Issue> Issues { get; set; } = [];
    public ICollection<Document> Documents { get; set; } = [];
}
