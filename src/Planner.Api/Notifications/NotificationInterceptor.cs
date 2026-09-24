using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Planner.Api.Authorization;
using Planner.Api.Common;
using Planner.Api.Realtime;
using Planner.Contracts.Realtime;
using Planner.Domain.Entities;
using Planner.Infrastructure;

namespace Planner.Api.Notifications;

/// <summary>
/// Turns every save into what follows from it: who now follows the issue, whose inbox the change lands
/// in, and which open feeds hear about it.
///
/// <para>It works from the change tracker rather than from each endpoint, for the same reason
/// <c>UpdatedAt</c> is stamped centrally: an endpoint that records activity cannot forget to notify.
/// Before the save it subscribes the people taking part — the creator and assignee of a new issue, a
/// new assignee, a commenter — and adds one notification per audit event per subscriber, other than
/// the person who acted. Those rows are part of the same SaveChanges, so an inbox never announces a
/// change that rolled back and a committed change is never missing from one.</para>
///
/// <para>After the save it publishes, never before: the new events to their team, so an open feed
/// grows, and the new notifications and unread counts to their recipients. A failure to publish is
/// logged and swallowed — the write already succeeded, and the next fetch shows it.</para>
///
/// <para>Scoped: it holds what one save produced until that save completes. Only the asynchronous
/// SaveChanges is intercepted, which is the only one the API calls.</para>
/// </summary>
public sealed class NotificationInterceptor(
    IHubContext<PlannerHub, IPlannerClient> hub,
    ILogger<NotificationInterceptor> logger) : SaveChangesInterceptor
{
    private List<ActivityEvent> _recorded = [];
    private List<Notification> _delivered = [];

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is PlannerDbContext db)
        {
            await PrepareAsync(db, cancellationToken);
        }

        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is PlannerDbContext db)
        {
            await PublishAsync(db, cancellationToken);
        }

        return result;
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        _recorded = [];
        _delivered = [];
        return Task.CompletedTask;
    }

    private async Task PrepareAsync(PlannerDbContext db, CancellationToken ct)
    {
        // EF detects changes after this interceptor runs; the assignee check below needs them now.
        db.ChangeTracker.DetectChanges();
        var entries = db.ChangeTracker.Entries().ToList();

        _recorded = entries
            .Where(e => e.State == EntityState.Added)
            .Select(e => e.Entity)
            .OfType<ActivityEvent>()
            .ToList();

        _delivered = [];

        var issueEvents = _recorded.Where(e => e.IssueId is not null && e.TeamId is not null).ToList();
        var participants = Participants(entries);

        if (issueEvents.Count == 0 && participants.Count == 0)
        {
            return;
        }

        var issueIds = issueEvents.Select(e => e.IssueId!.Value)
            .Concat(participants.Select(p => p.IssueId))
            .ToHashSet();

        var existing = await db.IssueSubscriptions.AsNoTracking()
            .Where(s => issueIds.Contains(s.IssueId))
            .Select(s => new { s.IssueId, s.UserId })
            .ToListAsync(ct);

        var subscribers = existing.Select(s => (s.IssueId, s.UserId)).ToHashSet();

        // A follow or unfollow in this very save counts: it is what the person asked for.
        foreach (var entry in entries.Where(e => e.Entity is IssueSubscription))
        {
            var subscription = (IssueSubscription)entry.Entity;
            if (entry.State == EntityState.Added) subscribers.Add((subscription.IssueId, subscription.UserId));
            if (entry.State == EntityState.Deleted) subscribers.Remove((subscription.IssueId, subscription.UserId));
        }

        foreach (var participant in participants)
        {
            if (subscribers.Add(participant))
            {
                db.IssueSubscriptions.Add(new IssueSubscription
                {
                    IssueId = participant.IssueId,
                    UserId = participant.UserId
                });
            }
        }

        if (issueEvents.Count == 0)
        {
            return;
        }

        var followersOf = subscribers
            .GroupBy(s => s.IssueId)
            .ToDictionary(g => g.Key, g => g.Select(s => s.UserId).ToList());

        var readers = await TeamReaders.LoadAsync(
            db,
            subscribers.Select(s => s.UserId).ToHashSet(),
            issueEvents.Select(e => e.TeamId!.Value).ToHashSet(),
            ct);

        foreach (var activity in issueEvents)
        {
            foreach (var userId in followersOf.GetValueOrDefault(activity.IssueId!.Value, []))
            {
                // Your own change is not news to you, and someone who can no longer read the team
                // keeps the subscription — access may come back — but receives nothing from it.
                if (userId == activity.ActorId || !readers.CanRead(userId, activity.TeamId!.Value))
                {
                    continue;
                }

                var notification = new Notification
                {
                    RecipientId = userId,
                    ActivityEventId = activity.Id,
                    IssueId = activity.IssueId.Value,
                    CreatedAt = activity.CreatedAt
                };

                db.Notifications.Add(notification);
                _delivered.Add(notification);
            }
        }
    }

    /// <summary>The people this save makes part of an issue: whoever files it, whoever it is assigned
    /// to, and whoever comments on it.</summary>
    private static HashSet<(Guid IssueId, Guid UserId)> Participants(IEnumerable<EntityEntry> entries)
    {
        var participants = new HashSet<(Guid, Guid)>();

        foreach (var entry in entries)
        {
            switch (entry.Entity)
            {
                case Issue issue when entry.State == EntityState.Added:
                    participants.Add((issue.Id, issue.CreatorId));
                    if (issue.AssigneeId is { } assignee) participants.Add((issue.Id, assignee));
                    break;

                case Issue issue when entry.State == EntityState.Modified
                                      && entry.Property(nameof(Issue.AssigneeId)).IsModified
                                      && issue.AssigneeId is { } newAssignee:
                    participants.Add((issue.Id, newAssignee));
                    break;

                case Comment comment when entry.State == EntityState.Added:
                    participants.Add((comment.IssueId, comment.AuthorId));
                    break;
            }
        }

        return participants;
    }

    private async Task PublishAsync(PlannerDbContext db, CancellationToken ct)
    {
        var recorded = _recorded;
        var delivered = _delivered;
        _recorded = [];
        _delivered = [];

        if (recorded.Count == 0)
        {
            return;
        }

        try
        {
            var events = await ActivityFeed.LoadAsync(db, recorded.Select(e => e.Id).ToList(), ct);

            foreach (var dto in events.Where(e => e.TeamId is not null))
            {
                await hub.Clients.Group(RealtimeGroups.Team(dto.TeamId!.Value)).ActivityRecorded(new(
                    ChangeKind.Created, EntityTypes.ActivityEvent, dto.Id, dto.TeamId, dto.ProjectId, dto.IssueId,
                    dto.Actor.Id, dto.CreatedAt, dto));
            }

            if (delivered.Count == 0)
            {
                return;
            }

            var ids = delivered.Select(n => n.Id).ToList();
            var notifications = await db.Notifications.AsNoTracking()
                .WithDetails()
                .Where(n => ids.Contains(n.Id))
                .OrderBy(n => n.CreatedAt)
                .ToListAsync(ct);

            foreach (var notification in notifications)
            {
                var dto = Mapping.ToNotification(notification);

                await hub.Clients.Group(RealtimeGroups.User(notification.RecipientId)).NotificationChanged(new(
                    ChangeKind.Created, EntityTypes.Notification, dto.Id, dto.TeamId, null, dto.Issue.Id,
                    dto.Event.Actor.Id, dto.CreatedAt, dto));
            }

            foreach (var recipient in notifications.Select(n => n.RecipientId).Distinct())
            {
                await hub.Clients.Group(RealtimeGroups.User(recipient))
                    .InboxChanged(new(await Inbox.UnreadAsync(db, recipient, ct)));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Publishing {EventCount} activity events and {NotificationCount} notifications failed",
                recorded.Count, delivered.Count);
        }
    }
}
