using Microsoft.AspNetCore.Identity;
using Planner.Domain.Entities;

namespace Planner.Domain.Identity;

public class AppUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public string TimeZone { get; set; } = "UTC";

    /// <summary>Deactivated users keep authoring history but cannot obtain tokens.</summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSeenAt { get; set; }

    public ICollection<TeamMember> TeamMemberships { get; set; } = [];
}
