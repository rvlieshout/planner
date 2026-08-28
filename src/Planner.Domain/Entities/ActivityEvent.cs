using System.Text.Json;
using Planner.Domain.Common;

namespace Planner.Domain.Entities;

/// <summary>Append-only audit trail. Powers the per-issue history feed, and lets a client that was
/// offline replay what it missed instead of refetching everything.</summary>
public class ActivityEvent : Entity
{
    public string EntityType { get; set; } = string.Empty;
    public Guid EntityId { get; set; }

    /// <summary>Denormalised scope columns so the feed can be filtered without joins.</summary>
    public Guid? TeamId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? IssueId { get; set; }

    public Guid ActorId { get; set; }
    public Identity.AppUser Actor { get; set; } = null!;

    /// <summary>Verb such as <c>issue.created</c> or <c>issue.state_changed</c>.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Changed fields as jsonb: <c>{"state":{"from":"Todo","to":"In Progress"}}</c>.</summary>
    public JsonDocument? Data { get; set; }
}
