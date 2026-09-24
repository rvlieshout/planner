using Microsoft.EntityFrameworkCore;
using Planner.Contracts.Issues;
using Planner.Domain.Entities;
using Planner.Infrastructure;

namespace Planner.Api.Common;

/// <summary>Turns audit rows into the feed's DTOs, naming the issue each one happened to.
///
/// <c>activity_events.issue_id</c> carries no foreign key — history outlives the issue — so the issue
/// is looked up in one query for the whole page rather than joined. An event whose issue has since been
/// deleted comes back with no reference, which is the truth.</summary>
public static class ActivityFeed
{
    public static async Task<List<ActivityEventDto>> ToDtosAsync(
        PlannerDbContext db,
        IReadOnlyList<ActivityEvent> events,
        CancellationToken ct)
    {
        var issues = await IssueReferencesAsync(
            db, events.Where(e => e.IssueId is not null).Select(e => e.IssueId!.Value).Distinct().ToList(), ct);

        return events
            .Select(e => Mapping.ToActivity(e) with
            {
                Issue = e.IssueId is { } issueId ? issues.GetValueOrDefault(issueId) : null
            })
            .ToList();
    }

    /// <summary>Loads the given events, with their actors, as DTOs in the order asked for.</summary>
    public static async Task<List<ActivityEventDto>> LoadAsync(
        PlannerDbContext db,
        IReadOnlyCollection<Guid> ids,
        CancellationToken ct)
    {
        var events = await db.ActivityEvents.AsNoTracking()
            .Include(a => a.Actor)
            .Where(a => ids.Contains(a.Id))
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct);

        return await ToDtosAsync(db, events, ct);
    }

    public static async Task<Dictionary<Guid, IssueReference>> IssueReferencesAsync(
        PlannerDbContext db,
        IReadOnlyCollection<Guid> issueIds,
        CancellationToken ct)
    {
        if (issueIds.Count == 0)
        {
            return [];
        }

        return await db.Issues.AsNoTracking()
            .Where(i => issueIds.Contains(i.Id))
            .Select(i => new IssueReference(i.Id, i.Team.Key + "-" + i.Number, i.Title))
            .ToDictionaryAsync(i => i.Id, ct);
    }
}
