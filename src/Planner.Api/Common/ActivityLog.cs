using System.Text.Json;
using Planner.Api.Authorization;
using Planner.Domain.Entities;
using Planner.Infrastructure;

namespace Planner.Api.Common;

/// <summary>Writes the audit trail. Rows are added to the same DbContext as the change itself, so a
/// failed write leaves no orphaned history behind — the caller's single SaveChanges commits both.</summary>
public interface IActivityLog
{
    void Record(
        string entityType,
        Guid entityId,
        string action,
        object? data = null,
        Guid? teamId = null,
        Guid? projectId = null,
        Guid? issueId = null);
}

public sealed class ActivityLog(PlannerDbContext db, CurrentUser user) : IActivityLog
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public void Record(
        string entityType,
        Guid entityId,
        string action,
        object? data = null,
        Guid? teamId = null,
        Guid? projectId = null,
        Guid? issueId = null)
    {
        db.ActivityEvents.Add(new ActivityEvent
        {
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            ActorId = user.Id,
            TeamId = teamId,
            ProjectId = projectId,
            IssueId = issueId,
            Data = data is null ? null : JsonSerializer.SerializeToDocument(data, SerializerOptions)
        });
    }
}

/// <summary>Audit verbs. Kept as constants so the client can switch on them and the docs can list
/// every value the feed will ever contain.</summary>
public static class ActivityActions
{
    public const string Created = "created";
    public const string Updated = "updated";
    public const string Archived = "archived";
    public const string Restored = "restored";
    public const string Deleted = "deleted";
    public const string StateChanged = "state_changed";
    public const string AssigneeChanged = "assignee_changed";
    public const string PriorityChanged = "priority_changed";
    public const string ProjectChanged = "project_changed";
    public const string MilestoneChanged = "milestone_changed";
    public const string LabelsChanged = "labels_changed";
    public const string Commented = "commented";
    public const string RelationAdded = "relation_added";
    public const string RelationRemoved = "relation_removed";
    public const string AttachmentAdded = "attachment_added";
    public const string MemberAdded = "member_added";
    public const string MemberRemoved = "member_removed";
    public const string MemberRoleChanged = "member_role_changed";
    public const string ActivityPurged = "activity_purged";
}
