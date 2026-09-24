using Microsoft.EntityFrameworkCore;
using Planner.Domain.Identity;
using Planner.Infrastructure;

namespace Planner.Api.Authorization;

/// <summary>Who, among a set of people, can read which of a set of teams — for questions about users
/// other than the caller, which <see cref="ITeamAccess"/> cannot answer.
///
/// The rule is <c>TeamAccess</c>'s: administrators read every team, everyone else the teams they are a
/// member of, and a deactivated account reads none. It is loaded in three small queries for the whole
/// set at once, so fanning an event out to twenty subscribers does not cost twenty lookups.</summary>
public sealed class TeamReaders
{
    private readonly HashSet<Guid> _active;
    private readonly HashSet<Guid> _admins;
    private readonly HashSet<(Guid User, Guid Team)> _members;

    private TeamReaders(HashSet<Guid> active, HashSet<Guid> admins, HashSet<(Guid, Guid)> members)
    {
        _active = active;
        _admins = admins;
        _members = members;
    }

    public bool CanRead(Guid userId, Guid teamId) =>
        _active.Contains(userId) && (_admins.Contains(userId) || _members.Contains((userId, teamId)));

    public static async Task<TeamReaders> LoadAsync(
        PlannerDbContext db,
        IReadOnlyCollection<Guid> userIds,
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken ct)
    {
        if (userIds.Count == 0)
        {
            return new TeamReaders([], [], []);
        }

        var active = await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id) && u.IsActive)
            .Select(u => u.Id)
            .ToListAsync(ct);

        var admins = await db.UserRoles.AsNoTracking()
            .Where(ur => userIds.Contains(ur.UserId))
            .Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, r.Name })
            .Where(x => x.Name == PlannerRoles.Owner || x.Name == PlannerRoles.Admin)
            .Select(x => x.UserId)
            .ToListAsync(ct);

        var members = await db.TeamMembers.AsNoTracking()
            .Where(m => userIds.Contains(m.UserId) && teamIds.Contains(m.TeamId))
            .Select(m => new { m.UserId, m.TeamId })
            .ToListAsync(ct);

        return new TeamReaders(
            [.. active],
            [.. admins],
            [.. members.Select(m => (m.UserId, m.TeamId))]);
    }
}
