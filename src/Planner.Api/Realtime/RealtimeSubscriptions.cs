using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Planner.Contracts.Realtime;
using Planner.Domain.Identity;
using Planner.Infrastructure;

namespace Planner.Api.Realtime;

/// <summary>Keeps each connection's team and issue groups equal to what its user may read now.
///
/// Endpoints that change what a user can read — membership, organisation role, deactivation — call
/// <see cref="SyncUserAsync"/> after saving, so the change reaches that user's open sockets without
/// the client having to ask. The hub uses the same rule on connect and on Resubscribe.
///
/// Access is read from the database rather than from the caller's token. A token keeps the role it
/// was issued with until it is refreshed; a socket that stops receiving a team the moment access is
/// revoked is the point of this class.</summary>
public sealed class RealtimeSubscriptions(
    IHubContext<PlannerHub, IPlannerClient> hub,
    RealtimeConnections connections,
    PlannerDbContext db,
    ILogger<RealtimeSubscriptions> logger)
{
    /// <summary>Re-evaluates every open connection of one user.</summary>
    public async Task SyncUserAsync(Guid userId, CancellationToken ct = default)
    {
        var open = connections.ForUser(userId);
        if (open.Count == 0)
        {
            return;
        }

        var access = await AccessAsync(userId, ct);

        foreach (var connection in open)
        {
            await connection.Gate.WaitAsync(ct);
            try
            {
                var groups = await ApplyAsync(connection, access, ct);
                await hub.Clients.Client(connection.ConnectionId).Subscribed(groups);
            }
            finally
            {
                connection.Gate.Release();
            }
        }
    }

    /// <summary>Re-evaluates one connection and returns the groups it is now in.</summary>
    public async Task<IReadOnlyList<string>> SyncConnectionAsync(string connectionId, CancellationToken ct = default)
    {
        var connection = connections.Find(connectionId)
                         ?? throw new InvalidOperationException($"Connection {connectionId} is not registered.");

        var access = await AccessAsync(connection.UserId, ct);

        await connection.Gate.WaitAsync(ct);
        try
        {
            return await ApplyAsync(connection, access, ct);
        }
        finally
        {
            connection.Gate.Release();
        }
    }

    /// <summary>Joins an issue's group, provided the connection's user can still read its team.</summary>
    public async Task<bool> JoinIssueAsync(string connectionId, Guid issueId, Guid teamId, CancellationToken ct = default)
    {
        if (connections.Find(connectionId) is not { } connection)
        {
            return false;
        }

        await connection.Gate.WaitAsync(ct);
        try
        {
            // A team this connection is not in may be one created, or joined, since it connected.
            // Asking again is cheaper than refusing an issue the user can open over REST.
            if (!connection.Teams.Contains(teamId))
            {
                await ApplyAsync(connection, await AccessAsync(connection.UserId, ct), ct);
            }

            if (!connection.Teams.Contains(teamId))
            {
                return false;
            }

            connection.Issues[issueId] = teamId;
            await hub.Groups.AddToGroupAsync(connectionId, RealtimeGroups.Issue(issueId), ct);
            return true;
        }
        finally
        {
            connection.Gate.Release();
        }
    }

    public async Task LeaveIssueAsync(string connectionId, Guid issueId, CancellationToken ct = default)
    {
        if (connections.Find(connectionId) is not { } connection)
        {
            return;
        }

        await connection.Gate.WaitAsync(ct);
        try
        {
            connection.Issues.TryRemove(issueId, out _);
            await hub.Groups.RemoveFromGroupAsync(connectionId, RealtimeGroups.Issue(issueId), ct);
        }
        finally
        {
            connection.Gate.Release();
        }
    }

    /// <summary>Moves one connection from the groups it is in to the ones it should be in. The caller
    /// holds the connection's gate.</summary>
    private async Task<IReadOnlyList<string>> ApplyAsync(
        RealtimeConnection connection,
        Access access,
        CancellationToken ct)
    {
        var id = connection.ConnectionId;
        var readable = access.Teams;

        connection.InOrganization = await SetAsync(connection.InOrganization, access.Organization, RealtimeGroups.Organization);
        connection.InAdministrators = await SetAsync(connection.InAdministrators, access.Administrator, RealtimeGroups.Administrators);

        async Task<bool> SetAsync(bool isIn, bool shouldBeIn, string group)
        {
            if (shouldBeIn && !isIn)
            {
                await hub.Groups.AddToGroupAsync(id, group, ct);
            }
            else if (isIn && !shouldBeIn)
            {
                await hub.Groups.RemoveFromGroupAsync(id, group, ct);
            }

            return shouldBeIn;
        }

        foreach (var teamId in readable.Except(connection.Teams))
        {
            await hub.Groups.AddToGroupAsync(id, RealtimeGroups.Team(teamId), ct);
        }

        foreach (var teamId in connection.Teams.Except(readable))
        {
            await hub.Groups.RemoveFromGroupAsync(id, RealtimeGroups.Team(teamId), ct);
        }

        foreach (var (issueId, _) in connection.Issues.Where(i => !readable.Contains(i.Value)).ToList())
        {
            connection.Issues.TryRemove(issueId, out _);
            await hub.Groups.RemoveFromGroupAsync(id, RealtimeGroups.Issue(issueId), ct);
        }

        if (!connection.Teams.SetEquals(readable))
        {
            logger.LogDebug("Connection {ConnectionId} of user {UserId} now reads {TeamCount} teams",
                id, connection.UserId, readable.Count);
        }

        connection.Teams = [.. readable];

        return
        [
            RealtimeGroups.User(connection.UserId),
            .. access.Organization ? [RealtimeGroups.Organization] : Array.Empty<string>(),
            .. access.Administrator ? [RealtimeGroups.Administrators] : Array.Empty<string>(),
            .. readable.Select(RealtimeGroups.Team)
        ];
    }

    /// <summary>What one user's sockets may hear.</summary>
    /// <param name="Teams">The teams they can read.</param>
    /// <param name="Organization">Directory changes: everyone but guests, as in the user directory.</param>
    /// <param name="Administrator">Directory changes about pending invitations as well.</param>
    private sealed record Access(IReadOnlySet<Guid> Teams, bool Organization, bool Administrator);

    /// <summary>The same rules as <c>TeamAccess.ReadableTeamIdsAsync</c> and the user directory, for any
    /// user rather than the caller: administrators read every team, everyone else the teams they are a
    /// member of, guests hear nothing about the directory, and a deactivated account hears nothing.</summary>
    private async Task<Access> AccessAsync(Guid userId, CancellationToken ct)
    {
        var isActive = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => (bool?)u.IsActive)
            .FirstOrDefaultAsync(ct);

        if (isActive != true)
        {
            return new Access(new HashSet<Guid>(), Organization: false, Administrator: false);
        }

        var roles = await db.UserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId)
            .Join(db.Roles, ur => ur.RoleId, r => r.Id, (_, r) => r.Name)
            .ToListAsync(ct);

        var isAdmin = roles.Contains(PlannerRoles.Owner) || roles.Contains(PlannerRoles.Admin);

        var teamIds = isAdmin
            ? await db.Teams.AsNoTracking().Select(t => t.Id).ToListAsync(ct)
            : await db.TeamMembers.AsNoTracking().Where(m => m.UserId == userId).Select(m => m.TeamId).ToListAsync(ct);

        // An account with no role at all is treated as a guest, as CurrentUser treats one.
        return new Access(teamIds.ToHashSet(), isAdmin || roles.Contains(PlannerRoles.Member), isAdmin);
    }
}
