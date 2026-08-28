using Planner.Domain.Common;

namespace Planner.Domain.Entities;

/// <summary>Markdown project documentation: specs, briefs, decision records. Always scoped to a team
/// so permissions can be resolved without walking to the project first; optionally tied to a project.</summary>
public class Document : Entity, IArchivable
{
    public Guid TeamId { get; set; }
    public Team Team { get; set; } = null!;

    public Guid? ProjectId { get; set; }
    public Project? Project { get; set; }

    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;

    public Guid CreatedById { get; set; }
    public Identity.AppUser CreatedBy { get; set; } = null!;

    public Guid? UpdatedById { get; set; }
    public Identity.AppUser? UpdatedBy { get; set; }

    public DateTimeOffset? ArchivedAt { get; set; }
}
