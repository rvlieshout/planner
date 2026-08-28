using Planner.Domain.Common;
using Planner.Contracts.Enums;

namespace Planner.Domain.Entities;

/// <summary>The unit of work: a task/issue. Belongs to exactly one team, optionally to a project and
/// one of that project's milestones, and may nest one level or more via <see cref="ParentId"/>.</summary>
public class Issue : Entity, IArchivable
{
    public Guid TeamId { get; set; }
    public Team Team { get; set; } = null!;

    /// <summary>Per-team sequence number. Combined with the team key this yields the display key ENG-42.</summary>
    public int Number { get; set; }

    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }

    public Guid StateId { get; set; }
    public WorkflowState State { get; set; } = null!;

    public IssuePriority Priority { get; set; } = IssuePriority.None;

    public Guid? AssigneeId { get; set; }
    public Identity.AppUser? Assignee { get; set; }

    public Guid CreatorId { get; set; }
    public Identity.AppUser Creator { get; set; } = null!;

    public Guid? ProjectId { get; set; }
    public Project? Project { get; set; }

    public Guid? MilestoneId { get; set; }
    public Milestone? Milestone { get; set; }

    public Guid? ParentId { get; set; }
    public Issue? Parent { get; set; }
    public ICollection<Issue> Children { get; set; } = [];

    /// <summary>Story points or hours; interpretation is a team convention.</summary>
    public int? Estimate { get; set; }

    public DateOnly? DueDate { get; set; }

    /// <summary>Fractional rank for manual board ordering. New items are inserted at the midpoint
    /// between neighbours so a reorder touches a single row.</summary>
    public double SortOrder { get; set; }

    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? CanceledAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }

    public ICollection<IssueLabel> Labels { get; set; } = [];
    public ICollection<Comment> Comments { get; set; } = [];
    public ICollection<Attachment> Attachments { get; set; } = [];
    public ICollection<IssueRelation> OutgoingRelations { get; set; } = [];
    public ICollection<IssueRelation> IncomingRelations { get; set; } = [];
}

public class IssueLabel
{
    public Guid IssueId { get; set; }
    public Issue Issue { get; set; } = null!;

    public Guid LabelId { get; set; }
    public Label Label { get; set; } = null!;
}

/// <summary>Directed link between two issues, stored once. "A blocks B" is not duplicated as
/// "B blocked by A" — the inverse is projected when reading issue B.</summary>
public class IssueRelation : Entity
{
    public Guid SourceIssueId { get; set; }
    public Issue SourceIssue { get; set; } = null!;

    public Guid TargetIssueId { get; set; }
    public Issue TargetIssue { get; set; } = null!;

    public IssueRelationType Type { get; set; }
}
