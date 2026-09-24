using Microsoft.EntityFrameworkCore;
using Planner.Domain.Entities;
using Planner.Domain.Identity;
using Planner.Infrastructure;

namespace Planner.Api.Notifications;

/// <summary>What one person's inbox holds.
///
/// A notification is only ever written for someone who could read the issue at the time, but access
/// can be taken away afterwards. The inbox follows access as it is now: someone removed from a team
/// stops seeing its entries — titles included — and they stop counting towards the badge. The rule is
/// expressed as a query, so the list, the count and the count pushed to someone else's socket all agree
/// without anyone's permissions being loaded into memory first.</summary>
public static class Inbox
{
    public static IQueryable<Notification> For(PlannerDbContext db, Guid userId) =>
        db.Notifications.Where(n => n.RecipientId == userId && (
            db.TeamMembers.Any(m => m.UserId == userId && m.TeamId == n.Issue.TeamId) ||
            db.UserRoles.Any(ur => ur.UserId == userId &&
                                   db.Roles.Any(r => r.Id == ur.RoleId &&
                                                     (r.Name == PlannerRoles.Owner || r.Name == PlannerRoles.Admin)))));

    public static Task<int> UnreadAsync(PlannerDbContext db, Guid userId, CancellationToken ct) =>
        For(db, userId).CountAsync(n => n.ReadAt == null, ct);

    /// <summary>With everything <see cref="Common.Mapping.ToNotification"/> reads.</summary>
    public static IQueryable<Notification> WithDetails(this IQueryable<Notification> query) =>
        query
            .Include(n => n.ActivityEvent).ThenInclude(a => a.Actor)
            .Include(n => n.Issue).ThenInclude(i => i.Team);
}
