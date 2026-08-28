using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Planner.Api.Authorization;
using Planner.Contracts.Realtime;
using Planner.Infrastructure;

namespace Planner.Api.Realtime;

/// <summary>Live change feed for connected clients. Connections are placed in a group per team the
/// caller can read, so the server never has to re-check permissions when publishing a change.</summary>
[Authorize]
public sealed class PlannerHub(ITeamAccess access, CurrentUser user, PlannerDbContext db, ILogger<PlannerHub> logger)
    : Hub<IPlannerClient>
{
    public override async Task OnConnectedAsync()
    {
        var groups = new List<string> { RealtimeGroups.User(user.Id), RealtimeGroups.Organization };

        foreach (var teamId in await access.ReadableTeamIdsAsync(Context.ConnectionAborted))
        {
            groups.Add(RealtimeGroups.Team(teamId));
        }

        foreach (var group in groups)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, group);
        }

        logger.LogDebug("Connection {ConnectionId} joined {GroupCount} groups", Context.ConnectionId, groups.Count);

        // Tell the client what it actually got, rather than letting it assume.
        await Clients.Caller.Subscribed(groups);
        await base.OnConnectedAsync();
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

        await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.Issue(issueId));
        return true;
    }

    public Task UnsubscribeFromIssue(Guid issueId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, RealtimeGroups.Issue(issueId));

    /// <summary>Re-evaluates team groups after the caller's membership changed, so a user added to a
    /// team starts receiving its traffic without reconnecting.</summary>
    public async Task<IReadOnlyList<string>> Resubscribe()
    {
        var groups = new List<string> { RealtimeGroups.User(user.Id), RealtimeGroups.Organization };

        foreach (var teamId in await access.ReadableTeamIdsAsync(Context.ConnectionAborted))
        {
            var group = RealtimeGroups.Team(teamId);
            groups.Add(group);
            await Groups.AddToGroupAsync(Context.ConnectionId, group);
        }

        await Clients.Caller.Subscribed(groups);
        return groups;
    }
}
