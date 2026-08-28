using Planner.Domain.Common;
using Planner.Contracts.Enums;

namespace Planner.Domain.Entities;

/// <summary>A column on the team board. Every issue points at exactly one state of its own team.</summary>
public class WorkflowState : Entity
{
    public Guid TeamId { get; set; }
    public Team Team { get; set; } = null!;

    public string Name { get; set; } = string.Empty;
    public WorkflowStateType Type { get; set; }
    public string Color { get; set; } = "#95A2B3";

    /// <summary>Left-to-right board ordering within the team.</summary>
    public int Position { get; set; }

    /// <summary>State new issues land in when the caller does not specify one.</summary>
    public bool IsDefault { get; set; }

    public ICollection<Issue> Issues { get; set; } = [];
}
