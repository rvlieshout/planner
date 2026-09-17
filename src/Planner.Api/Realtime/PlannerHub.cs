using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Planner.Api.Authorization;
using Planner.Contracts.Realtime;
using Planner.Infrastructure;

namespace Planner.Api.Realtime;

/// <summary>Live change feed for connected clients. Connections are placed in a group per team the
/// caller can read, so the server never has to re-check permissions when publishing a change. Those
/// groups follow the caller's access for the life of the connection: see
/// <see cref="IRealtimeSubscriptions"/>.</summary>
[Authorize]
public sealed class PlannerHub(
    ITeamAccess access,
    CurrentUser user,
    PlannerDbContext db,
    RealtimeConnections connections,
    IRealtimeSubscriptions subscriptions,
    ILogger<PlannerHub> logger)
    : Hub<IPlannerClient>
{
    public override async Task OnConnectedAsync()
    {
        connections.Register(Context.ConnectionId, user.Id);

        await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.User(user.Id));
        await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.Organization);

        var groups = await subscriptions.SyncConnectionAsync(Context.ConnectionId, Context.ConnectionAborted);

        logger.LogDebug("Connection {ConnectionId} joined {GroupCount} groups", Context.ConnectionId, groups.Count);

        // Tell the client what it actually got, rather than letting it assume.
        await Clients.Caller.Subscribed(groups);
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        // SignalR drops the connection from its groups by itself; this only forgets which they were.
        connections.Unregister(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>Opt into comment, attachment and relation traffic for one issue — typically the issue
    /// the user has open in a detail pane.</summary>
    public async Task<bool> SubscribeToIssue(Guid issueId)
    {
        var teamId = await db.Issues
            .Where(i => i.Id == issueId)
            .Select(i => (Guid?)i.TeamId)
            .FirstOrDefaultAsync(Context.ConnectionAborted);

        if (teamId is null || !await access.HasAsync(teamId.Value, TeamPermission.Read, Context.ConnectionAborted))
        {
            return false;
        }

        return await subscriptions.JoinIssueAsync(Context.ConnectionId, issueId, teamId.Value, Context.ConnectionAborted);
    }

    public Task UnsubscribeFromIssue(Guid issueId) =>
        subscriptions.LeaveIssueAsync(Context.ConnectionId, issueId, Context.ConnectionAborted);

    /// <summary>Re-evaluates team groups against the caller's current access, joining teams they were
    /// added to and leaving teams they were removed from, without reconnecting.</summary>
    public async Task<IReadOnlyList<string>> Resubscribe()
    {
        var groups = await subscriptions.SyncConnectionAsync(Context.ConnectionId, Context.ConnectionAborted);

        await Clients.Caller.Subscribed(groups);
        return groups;
    }
}
