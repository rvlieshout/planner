using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Planner.Api.Authorization;
using Planner.Api.Common;
using Planner.Api.Notifications;
using Planner.Api.Realtime;
using Planner.Contracts.Common;
using Planner.Contracts.Issues;
using Planner.Contracts.Realtime;
using Planner.Infrastructure;

namespace Planner.Api.Endpoints;

/// <summary>The caller's inbox: what happened to the issues they follow. Every route is about the
/// caller's own notifications, so there is nothing to authorise beyond being signed in — someone
/// else's notification is simply not found.</summary>
public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var inbox = app.MapGroup("/api/v1/notifications").WithTags("Inbox");

        inbox.MapGet("/", ListAsync).WithSummary("Your inbox, newest first");
        inbox.MapGet("/status", StatusAsync).WithSummary("How many inbox entries are unread");
        inbox.MapPatch("/{id:b58}", UpdateAsync).WithSummary("Mark one entry read or unread");
        inbox.MapPost("/read", ReadAllAsync).WithSummary("Mark everything read, or everything about one issue");

        return app;
    }

    private static async Task<IResult> ListAsync(
        PlannerDbContext db,
        CurrentUser current,
        [AsParameters] PageQuery paging,
        bool? unread,
        CancellationToken ct)
    {
        var query = Inbox.For(db, current.Id).AsNoTracking();

        if (unread == true)
        {
            query = query.Where(n => n.ReadAt == null);
        }

        var total = await query.LongCountAsync(ct);

        var items = await query
            .WithDetails()
            .OrderByDescending(n => n.CreatedAt)
            .ThenByDescending(n => n.Id)
            .Skip(paging.Skip)
            .Take(paging.NormalizedSize)
            .ToListAsync(ct);

        return Results.Ok(new PagedResult<NotificationDto>(
            items.Select(Mapping.ToNotification).ToList(), paging.NormalizedPage, paging.NormalizedSize, total));
    }

    private static async Task<IResult> StatusAsync(PlannerDbContext db, CurrentUser current, CancellationToken ct) =>
        Results.Ok(new InboxStatus(await Inbox.UnreadAsync(db, current.Id, ct)));

    private static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateNotificationRequest request,
        PlannerDbContext db,
        CurrentUser current,
        IHubContext<PlannerHub, IPlannerClient> hub,
        CancellationToken ct)
    {
        var notification = await Inbox.For(db, current.Id).WithDetails().FirstOrDefaultAsync(n => n.Id == id, ct);
        if (notification is null)
        {
            return ApiResults.NotFound("That notification");
        }

        var readAt = request.Read ? notification.ReadAt ?? DateTimeOffset.UtcNow : (DateTimeOffset?)null;

        if (readAt != notification.ReadAt)
        {
            notification.ReadAt = readAt;
            await db.SaveChangesAsync(ct);
            await PublishStatusAsync(db, hub, current.Id, ct);
        }

        return Results.Ok(Mapping.ToNotification(notification));
    }

    /// <summary>Opening an issue reads everything about it, which is what <paramref name="issueId"/>
    /// is for; without it this is "mark all read".</summary>
    private static async Task<IResult> ReadAllAsync(
        PlannerDbContext db,
        CurrentUser current,
        IHubContext<PlannerHub, IPlannerClient> hub,
        Guid? issueId,
        CancellationToken ct)
    {
        var query = Inbox.For(db, current.Id).Where(n => n.ReadAt == null);

        if (issueId is { } issue)
        {
            query = query.Where(n => n.IssueId == issue);
        }

        var now = DateTimeOffset.UtcNow;
        var changed = await query.ExecuteUpdateAsync(n => n
            .SetProperty(x => x.ReadAt, now)
            .SetProperty(x => x.UpdatedAt, now), ct);

        var status = new InboxStatus(await Inbox.UnreadAsync(db, current.Id, ct));

        if (changed > 0)
        {
            await hub.Clients.Group(RealtimeGroups.User(current.Id)).InboxChanged(status);
        }

        return Results.Ok(status);
    }

    // Every connection of this user, this one included: the badge in a second tab moves too.
    private static async Task PublishStatusAsync(
        PlannerDbContext db,
        IHubContext<PlannerHub, IPlannerClient> hub,
        Guid userId,
        CancellationToken ct) =>
        await hub.Clients.Group(RealtimeGroups.User(userId))
            .InboxChanged(new InboxStatus(await Inbox.UnreadAsync(db, userId, ct)));
}
