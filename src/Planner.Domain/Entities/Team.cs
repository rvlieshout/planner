using Planner.Domain.Common;
using Planner.Contracts.Enums;

namespace Planner.Domain.Entities;

/// <summary>Top-level container in a single-organisation install. Owns its own workflow states,
/// labels, projects and issue numbering sequence.</summary>
public class Team : Entity, IArchivable
{
    /// <summary>Short uppercase prefix used to build human issue keys, e.g. "ENG" -> ENG-42.</summary>
    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Color { get; set; } = "#6E79F1";

    /// <summary>Private teams are invisible to non-members, including org admins' list endpoints.</summary>
    public bool IsPrivate { get; set; }

    /// <summary>Monotonic per-team issue counter. Incremented with an atomic UPDATE .. RETURNING,
    /// never read-then-write, so concurrent creates cannot collide.</summary>
    public int IssueCounter { get; set; }

    public DateTimeOffset? ArchivedAt { get; set; }

    public ICollection<TeamMember> Members { get; set; } = [];
    public ICollection<WorkflowState> WorkflowStates { get; set; } = [];
    public ICollection<Label> Labels { get; set; } = [];
    public ICollection<Project> Projects { get; set; } = [];
    public ICollection<Issue> Issues { get; set; } = [];
}

/// <summary>Join entity between a user and a team, carrying the team-scoped role.</summary>
public class TeamMember
{
    public Guid TeamId { get; set; }
    public Team Team { get; set; } = null!;

    public Guid UserId { get; set; }
    public Identity.AppUser User { get; set; } = null!;

    public TeamRole Role { get; set; } = TeamRole.Member;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
