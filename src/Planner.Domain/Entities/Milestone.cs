using Planner.Domain.Common;
using Planner.Contracts.Enums;

namespace Planner.Domain.Entities;

/// <summary>An ordered checkpoint inside a project. Issues may be attached to at most one milestone,
/// and progress is derived from those issues rather than stored.</summary>
public class Milestone : Entity
{
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateOnly? TargetDate { get; set; }
    public MilestoneStatus Status { get; set; } = MilestoneStatus.Upcoming;

    /// <summary>Position among the project's milestones, as a <see cref="Common.Rank"/> key.</summary>
    public required string Rank { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public ICollection<Issue> Issues { get; set; } = [];
}
