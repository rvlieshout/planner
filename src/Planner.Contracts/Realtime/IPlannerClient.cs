using Planner.Contracts.Auth;
using Planner.Contracts.Issues;
using Planner.Contracts.Projects;
using Planner.Contracts.Teams;

namespace Planner.Contracts.Realtime;

/// <summary>Server-to-client SignalR surface. A .NET client references this so both ends are bound
/// to the same method names and payload shapes at compile time rather than by convention.</summary>
public interface IPlannerClient
{
    Task TeamChanged(EntityChange<TeamDto> change);
    Task TeamMemberChanged(EntityChange<TeamMemberDto> change);
    Task WorkflowStateChanged(EntityChange<WorkflowStateDto> change);
    Task LabelChanged(EntityChange<LabelDto> change);
    Task ProjectChanged(EntityChange<ProjectDto> change);
    Task MilestoneChanged(EntityChange<MilestoneDto> change);
    Task DocumentChanged(EntityChange<DocumentSummary> change);
    Task IssueChanged(EntityChange<IssueSummary> change);
    Task CommentChanged(EntityChange<CommentDto> change);
    Task AttachmentChanged(EntityChange<AttachmentDto> change);
    Task IssueRelationChanged(EntityChange<IssueRelationDto> change);
    Task UserChanged(EntityChange<UserSummary> change);

    /// <summary>An audit event, as it is written. Sent to the team group, so an open activity feed or
    /// issue history grows without polling.</summary>
    Task ActivityRecorded(EntityChange<ActivityEventDto> change);

    /// <summary>An administrator deleted part of a team's or project's history.</summary>
    Task ActivityPurged(ActivityPurge purge);

    /// <summary>A new inbox entry, sent only to its recipient.</summary>
    Task NotificationChanged(EntityChange<NotificationDto> change);

    /// <summary>The recipient's unread count, after anything moved it — including a read in another tab.</summary>
    Task InboxChanged(InboxStatus status);

    /// <summary>Sent to the caller's own connection after it joins, so the client knows which groups
    /// the server actually granted rather than assuming its join requests all succeeded.</summary>
    Task Subscribed(IReadOnlyList<string> groups);
}
