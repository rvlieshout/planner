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
public interface IRealtimeSubscriptions
{
    /// <summary>Re-evaluates every open connection of one user.</summary>
    Task SyncUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Re-evaluates one connection and returns the groups it is now in.</summary>
    Task<IReadOnlyList<string>> SyncConnectionAsync(string connectionId, CancellationToken ct = default);

    /// <summary>Joins an issue's group, provided the connection's user can still read its team.</summary>
    Task<bool> JoinIssueAsync(string connectionId, Guid issueId, Guid teamId, CancellationToken ct = default);

    Task LeaveIssueAsync(string connectionId, Guid issueId, CancellationToken ct = default);
}

public sealed class RealtimeSubscriptions(
    IHubContext<PlannerHub, IPlannerClient> hub,
    RealtimeConnections connections,
    PlannerDbContext db,
    ILogger<RealtimeSubscriptions> logger) : IRealtimeSubscriptions
{
    public async Task SyncUserAsync(Guid userId, CancellationToken ct = default)
    {
        var open = connections.ForUser(userId);
        if (open.Count == 0)
        {
            return;
        }

        var readable = await ReadableTeamIdsAsync(userId, ct);

        foreach (var connection in open)
        {
            await connection.Gate.WaitAsync(ct);
            try
            {
                var groups = await ApplyAsync(connection, readable, ct);
                await hub.Clients.Client(connection.ConnectionId).Subscribed(groups);
            }
            finally
            {
                connection.Gate.Release();
            }
        }
    }

    public async Task<IReadOnlyList<string>> SyncConnectionAsync(string connectionId, CancellationToken ct = default)
    {
        var connection = connections.Find(connectionId)
                         ?? throw new InvalidOperationException($"Connection {connectionId} is not registered.");

        var readable = await ReadableTeamIdsAsync(connection.UserId, ct);

        await connection.Gate.WaitAsync(ct);
        try
        {
            return await ApplyAsync(connection, readable, ct);
        }
        finally
        {
            connection.Gate.Release();
        }
    }

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
                await ApplyAsync(connection, await ReadableTeamIdsAsync(connection.UserId, ct), ct);
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
            connection.Issues.Remove(issueId);
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
        IReadOnlySet<Guid> readable,
        CancellationToken ct)
    {
        var id = connection.ConnectionId;

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
            connection.Issues.Remove(issueId);
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
            RealtimeGroups.Organization,
            .. readable.Select(RealtimeGroups.Team)
        ];
    }

    /// <summary>The same rule as <c>TeamAccess.ReadableTeamIdsAsync</c>, for any user rather than the
    /// caller: administrators read every team, everyone else the teams they are a member of, and a
    /// deactivated account reads none.</summary>
    private async Task<IReadOnlySet<Guid>> ReadableTeamIdsAsync(Guid userId, CancellationToken ct)
    {
        var isActive = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => (bool?)u.IsActive)
            .FirstOrDefaultAsync(ct);

        if (isActive != true)
        {
            return new HashSet<Guid>();
        }

        var isAdmin = await db.UserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId)
            .Join(db.Roles, ur => ur.RoleId, r => r.Id, (_, r) => r.Name)
            .AnyAsync(name => name == PlannerRoles.Owner || name == PlannerRoles.Admin, ct);

        var teamIds = isAdmin
            ? await db.Teams.AsNoTracking().Select(t => t.Id).ToListAsync(ct)
            : await db.TeamMembers.AsNoTracking().Where(m => m.UserId == userId).Select(m => m.TeamId).ToListAsync(ct);

        return teamIds.ToHashSet();
    }
}
