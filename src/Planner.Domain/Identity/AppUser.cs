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

    /// <summary>Only invitation creation leaves the email unconfirmed and the password unset.
    /// Existing accounts, including deactivated ones, must never become invitations.</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public bool IsInvitationPending => !EmailConfirmed && PasswordHash is null;

    /// <summary>When the latest invitation link stops working. Null once it is accepted or revoked.
    /// Informational only: the link itself is validated by its own expiry and the security stamp.</summary>
    public DateTimeOffset? InvitationExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSeenAt { get; set; }

    public ICollection<TeamMember> TeamMemberships { get; set; } = [];
}
