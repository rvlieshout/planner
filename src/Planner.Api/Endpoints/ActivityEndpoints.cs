using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Planner.Api.Auth;
using Planner.Api.Common;
using Planner.Api.Notifications;
using Planner.Api.Realtime;
using Planner.Contracts.Issues;
using Planner.Contracts.Realtime;
using Planner.Infrastructure;

namespace Planner.Api.Endpoints;

/// <summary>Deleting history. Reading it lives with the issue endpoints, next to the rows it describes.</summary>
public static class ActivityEndpoints
{
    public static IEndpointRouteBuilder MapActivityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/api/v1/activity", PurgeAsync)
            .WithTags("Activity")
            .RequireAuthorization(PlannerPolicies.OrgAdmin)
            .WithSummary("Delete a project's or a team's history, all of it or what is older than a number of days");

        return app;
    }

    /// <summary>
    /// Removes audit rows for one project or one team: every one, or those older than
    /// <paramref name="olderThanDays"/>. The inbox entries that pointed at them go with them.
    ///
    /// <para>History is the one thing an organisation has to be able to trust, so this is for
    /// administrators alone, and it leaves a row of its own — who cleared what, and how much — written
    /// in the same transaction as the delete. That row is fresh, so it survives any later purge by age.</para>
    /// </summary>
    private static async Task<IResult> PurgeAsync(
        PlannerDbContext db,
        IActivityLog activity,
        IHubContext<PlannerHub, IPlannerClient> hub,
        Guid? projectId,
        Guid? teamId,
        int? olderThanDays,
        CancellationToken ct)
    {
        if (projectId is null == teamId is null)
        {
            return ApiResults.BadRequest("Name either a project or a team whose history to delete.");
        }

        var validation = new Validation().Range(olderThanDays, 1, 36_500, "olderThanDays");
        if (validation.HasErrors)
        {
            return validation.ToResult();
        }

        Guid scopeTeamId;
        if (projectId is { } project)
        {
            var owner = await db.Projects.Where(p => p.Id == project).Select(p => (Guid?)p.TeamId).FirstOrDefaultAsync(ct);
            if (owner is null)
            {
                return ApiResults.NotFound("That project");
            }

            scopeTeamId = owner.Value;
        }
        else
        {
            if (!await db.Teams.AnyAsync(t => t.Id == teamId, ct))
            {
                return ApiResults.NotFound("That team");
            }

            scopeTeamId = teamId!.Value;
        }

        DateTimeOffset? before = olderThanDays is { } days ? DateTimeOffset.UtcNow.AddDays(-days) : null;

        var doomed = projectId is { } p1
            ? db.ActivityEvents.Where(a => a.ProjectId == p1)
            : db.ActivityEvents.Where(a => a.TeamId == scopeTeamId);

        if (before is { } cutoff)
        {
            doomed = doomed.Where(a => a.CreatedAt < cutoff);
        }

        var deleted = 0;
        List<Guid> recipients = [];

        // Retries are enabled on the context, so a user-opened transaction has to run inside the
        // execution strategy to be replayable as a whole.
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            // Whose unread badge is about to move, read before the rows it counts are gone.
            recipients = await db.Notifications
                .Where(n => n.ReadAt == null && doomed.Select(a => a.Id).Contains(n.ActivityEventId))
                .Select(n => n.RecipientId)
                .Distinct()
                .ToListAsync(ct);

            // Notifications go by the foreign key's cascade.
            deleted = await doomed.ExecuteDeleteAsync(ct);

            activity.Record(
                projectId is null ? EntityTypes.Team : EntityTypes.Project,
                projectId ?? scopeTeamId,
                ActivityActions.ActivityPurged,
                new { olderThanDays, deleted },
                teamId: scopeTeamId,
                projectId: projectId);

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        });

        var purge = new ActivityPurge(scopeTeamId, projectId, before, deleted);

        await hub.Clients.Group(RealtimeGroups.Team(scopeTeamId)).ActivityPurged(purge);

        foreach (var recipient in recipients)
        {
            await hub.Clients.Group(RealtimeGroups.User(recipient))
                .InboxChanged(new InboxStatus(await Inbox.UnreadAsync(db, recipient, ct)));
        }

        return Results.Ok(purge);
    }
}
