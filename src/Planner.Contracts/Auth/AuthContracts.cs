using Planner.Contracts.Common;

namespace Planner.Contracts.Auth;

/// <summary>The caller's own profile, org role and team memberships — the first call a client makes
/// after signing in, so it can build its sidebar without three more round trips.</summary>
public sealed record MeResponse(
    Guid Id,
    string Email,
    string DisplayName,
    string? AvatarUrl,
    string TimeZone,
    string Role,
    bool IsActive,
    IReadOnlyList<MeTeamMembership> Teams);

public sealed record MeTeamMembership(Guid TeamId, string TeamKey, string TeamName, string Role);

public sealed record UserSummary(Guid Id, string Email, string DisplayName, string? AvatarUrl, bool IsActive);

public sealed record UserDetail(
    Guid Id,
    string Email,
    string DisplayName,
    string? AvatarUrl,
    string TimeZone,
    string Role,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSeenAt);

public sealed record CreateUserRequest(
    string Email,
    string Password,
    string DisplayName,
    string Role = "member",
    string TimeZone = "UTC");

public sealed record UpdateProfileRequest(
    Optional<string> DisplayName,
    Optional<string?> AvatarUrl,
    Optional<string> TimeZone);

public sealed record UpdateUserRequest(
    Optional<string> DisplayName,
    Optional<string?> AvatarUrl,
    Optional<string> TimeZone,
    Optional<bool> IsActive,
    Optional<string> Role);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record ResetPasswordRequest(string NewPassword);
