using Microsoft.EntityFrameworkCore;
using Planner.Domain.Enums;
using Planner.Infrastructure;

namespace Planner.Api.Authorization;

/// <summary>What the caller may do inside one team. Resolved from the pair (organisation role,
/// team role) — see docs/roles-and-permissions.md for the full matrix.</summary>
public enum TeamPermission
{
    None = 0,

    /// <summary>See the team, its projects, issues and documents.</summary>
    Read = 1,

    /// <summary>Read, plus post comments and attachments.</summary>
    Comment = 2,

    /// <summary>Comment, plus create and edit projects, milestones, documents and issues.</summary>
    Write = 3,

    /// <summary>Write, plus manage team settings, membership, workflow states and labels.</summary>
    Administer = 4
}

public interface ITeamAccess
{
    Task<TeamPermission> GetPermissionAsync(Guid teamId, CancellationToken ct = default);

    Task<bool> HasAsync(Guid teamId, TeamPermission required, CancellationToken ct = default);

    /// <summary>Team ids the caller may at least read. Used to scope every cross-team list query.</summary>
    Task<IReadOnlyList<Guid>> ReadableTeamIdsAsync(CancellationToken ct = default);
}

public sealed class TeamAccess(PlannerDbContext db, CurrentUser user) : ITeamAccess
{
    // Memberships are read repeatedly within a request (list endpoints check one team per row),
    // so the lookup is cached for the lifetime of this scoped service.
    private Dictionary<Guid, TeamRole>? _memberships;

    public async Task<TeamPermission> GetPermissionAsync(Guid teamId, CancellationToken ct = default)
    {
        var memberships = await GetMembershipsAsync(ct);
        var isMember = memberships.TryGetValue(teamId, out var teamRole);

        // Organisation admins administer every team; they can grant themselves membership anyway,
        // and an on-prem operator locked out of a team is a support call, not a security boundary.
        if (user.IsAdmin)
        {
            return TeamPermission.Administer;
        }

        if (!isMember)
        {
            return TeamPermission.None;
        }

        // Guests are capped at commenting no matter which team role they were given.
        if (user.IsGuest)
        {
            return TeamPermission.Comment;
        }

        return teamRole switch
        {
            TeamRole.Lead => TeamPermission.Administer,
            TeamRole.Member => TeamPermission.Write,
            TeamRole.Viewer => TeamPermission.Read,
            _ => TeamPermission.None
        };
    }

    public async Task<bool> HasAsync(Guid teamId, TeamPermission required, CancellationToken ct = default) =>
        await GetPermissionAsync(teamId, ct) >= required;

    public async Task<IReadOnlyList<Guid>> ReadableTeamIdsAsync(CancellationToken ct = default)
    {
        if (user.IsAdmin)
        {
            return await db.Teams.Select(t => t.Id).ToListAsync(ct);
        }

        var memberships = await GetMembershipsAsync(ct);
        return memberships.Keys.ToList();
    }

    private async Task<Dictionary<Guid, TeamRole>> GetMembershipsAsync(CancellationToken ct)
    {
        return _memberships ??= await db.TeamMembers
            .Where(m => m.UserId == user.Id)
            .ToDictionaryAsync(m => m.TeamId, m => m.Role, ct);
    }
}
