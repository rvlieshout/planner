using Microsoft.EntityFrameworkCore;
using Planner.Api.Authorization;
using Planner.Api.Common;
using Planner.Contracts.Auth;
using Planner.Domain.Entities;
using Planner.Infrastructure;

namespace Planner.Api.Endpoints;

/// <summary>Who follows an issue. Following yourself needs only read access — a viewer may want to know
/// when something they are waiting on moves. Adding or removing someone else is an edit to the issue, so
/// it needs write access, and the person added must be able to read the team: an inbox entry they cannot
/// open is worse than none.
///
/// <para>An archived issue gains no followers — nothing will happen to it that is worth hearing about,
/// and it is read-only in every other respect. Leaving one is still allowed: that changes nothing on the
/// issue, only what reaches your own inbox.</para>
///
/// <para>Every route answers with the full list, so a client can replace what it shows.</para></summary>
public static class IssueSubscriptionEndpoints
{
    public static void MapIssueSubscriptions(this IEndpointRouteBuilder app)
    {
        var subscribers = app.MapGroup("/api/v1/issues/{id:b58}/subscribers").WithTags("Issues");

        subscribers.MapGet("/", ListAsync).WithSummary("People following an issue");
        subscribers.MapPut("/{userId:b58}", AddAsync).WithSummary("Follow an issue, or add someone as a follower");
        subscribers.MapDelete("/{userId:b58}", RemoveAsync).WithSummary("Unfollow an issue, or remove a follower");
    }

    private static async Task<IResult> ListAsync(
        Guid id,
        PlannerDbContext db,
        ITeamAccess access,
        CancellationToken ct)
    {
        if (await AuthorizeAsync(db, access, id, TeamPermission.Read, ct) is { Denied: { } denied })
        {
            return denied;
        }

        return Results.Ok(await SubscribersAsync(db, id, ct));
    }

    private static async Task<IResult> AddAsync(
        Guid id,
        Guid userId,
        PlannerDbContext db,
        ITeamAccess access,
        CurrentUser current,
        CancellationToken ct)
    {
        var required = userId == current.Id ? TeamPermission.Read : TeamPermission.Write;
        var (teamId, archivedAt, denied) = await AuthorizeAsync(db, access, id, required, ct);
        if (denied is not null)
        {
            return denied;
        }

        if (archivedAt is not null)
        {
            return ApiResults.Conflict("This issue is archived and cannot gain followers. Restore it first.");
        }

        if (userId != current.Id)
        {
            var readers = await TeamReaders.LoadAsync(db, [userId], [teamId], ct);
            if (!readers.CanRead(userId, teamId))
            {
                return ApiResults.BadRequest("That person cannot see this team, so they cannot follow its issues.");
            }
        }

        if (!await db.IssueSubscriptions.AnyAsync(s => s.IssueId == id && s.UserId == userId, ct))
        {
            db.IssueSubscriptions.Add(new IssueSubscription { IssueId = id, UserId = userId });
            await db.SaveChangesAsync(ct);
        }

        return Results.Ok(await SubscribersAsync(db, id, ct));
    }

    private static async Task<IResult> RemoveAsync(
        Guid id,
        Guid userId,
        PlannerDbContext db,
        ITeamAccess access,
        CurrentUser current,
        CancellationToken ct)
    {
        var required = userId == current.Id ? TeamPermission.Read : TeamPermission.Write;
        if (await AuthorizeAsync(db, access, id, required, ct) is { Denied: { } denied })
        {
            return denied;
        }

        await db.IssueSubscriptions.Where(s => s.IssueId == id && s.UserId == userId).ExecuteDeleteAsync(ct);

        return Results.Ok(await SubscribersAsync(db, id, ct));
    }

    private static async Task<(Guid TeamId, DateTimeOffset? ArchivedAt, IResult? Denied)> AuthorizeAsync(
        PlannerDbContext db,
        ITeamAccess access,
        Guid issueId,
        TeamPermission required,
        CancellationToken ct)
    {
        var issue = await db.Issues.Where(i => i.Id == issueId)
            .Select(i => new { i.TeamId, i.ArchivedAt })
            .FirstOrDefaultAsync(ct);

        if (issue is null)
        {
            return (Guid.Empty, null, ApiResults.NotFound("That issue"));
        }

        return (issue.TeamId, issue.ArchivedAt, await ApiResults.RequireTeamAsync(access, issue.TeamId, required, ct));
    }

    private static Task<List<UserSummary>> SubscribersAsync(PlannerDbContext db, Guid issueId, CancellationToken ct) =>
        db.IssueSubscriptions.AsNoTracking()
            .Where(s => s.IssueId == issueId)
            .Select(s => s.User)
            .OrderBy(u => u.DisplayName)
            .Select(Mapping.UserSummaryProjection)
            .ToListAsync(ct);
}
