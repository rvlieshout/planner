using Planner.Domain.Common;

namespace Planner.Domain.Entities;

/// <summary>Tag applied to issues. A null <see cref="TeamId"/> makes the label organisation-wide
/// and usable by every team; org labels are managed by admins only.</summary>
public class Label : Entity
{
    public Guid? TeamId { get; set; }
    public Team? Team { get; set; }

    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = "#95A2B3";
    public string? Description { get; set; }

    public ICollection<IssueLabel> IssueLabels { get; set; } = [];
}
