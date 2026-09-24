namespace Planner.Domain.Entities;

/// <summary>A person following one issue: whatever happens to it lands in their inbox.
///
/// Taking part subscribes you — filing the issue, being assigned it, commenting on it — and anyone can
/// follow or unfollow an issue they can read. A member can also add or remove someone else, which is
/// how a person is "attached" to an issue they have not touched yet.</summary>
public class IssueSubscription
{
    public Guid IssueId { get; set; }
    public Issue Issue { get; set; } = null!;

    public Guid UserId { get; set; }
    public Identity.AppUser User { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
