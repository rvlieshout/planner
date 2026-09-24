using Planner.Domain.Common;

namespace Planner.Domain.Entities;

/// <summary>One activity event, delivered to one subscriber of the issue it happened to.
///
/// The event itself is not copied: the row points at it, so the inbox and the history feed can never
/// tell two different stories about the same change. Deleting the issue deletes its notifications —
/// an inbox entry that cannot be opened is noise.</summary>
public class Notification : Entity
{
    public Guid RecipientId { get; set; }
    public Identity.AppUser Recipient { get; set; } = null!;

    public Guid ActivityEventId { get; set; }
    public ActivityEvent ActivityEvent { get; set; } = null!;

    public Guid IssueId { get; set; }
    public Issue Issue { get; set; } = null!;

    public DateTimeOffset? ReadAt { get; set; }
}
