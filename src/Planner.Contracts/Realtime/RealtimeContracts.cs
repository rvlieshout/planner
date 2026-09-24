using Planner.Contracts.Common;

namespace Planner.Contracts.Realtime;

public enum ChangeKind
{
    Created = 0,
    Updated = 1,
    Deleted = 2,
    Archived = 3,
    Restored = 4
}

/// <summary>Envelope pushed over SignalR for every mutation. Carries the scope ids so a client can
/// decide whether the change affects a view it currently has open without parsing the payload.</summary>
/// <param name="Entity">The new state, or null for <see cref="ChangeKind.Deleted"/>.</param>
public sealed record EntityChange<T>(
    ChangeKind Kind,
    string EntityType,
    Guid Id,
    Guid? TeamId,
    Guid? ProjectId,
    Guid? IssueId,
    Guid ActorId,
    DateTimeOffset OccurredAt,
    T? Entity);

/// <summary>SignalR group names. Every event is published to exactly one group, so a connection that
/// is in several of them never receives the same change twice.
/// <list type="bullet">
/// <item>Team — board-level traffic: the team itself, its states, labels, members, projects,
/// milestones, documents and issues. Joined automatically for every team the caller can read.</item>
/// <item>Issue — comment, attachment and relation traffic for the one issue a client has open.
/// Joined on demand so an open board does not stream every comment in the team.</item>
/// <item>User — messages addressed to one person across all their connections.</item>
/// <item>Organization — directory-level changes such as a user being added or deactivated.</item>
/// </list></summary>
public static class RealtimeGroups
{
    // Base58, like every other id the client sees. The names are sent to the caller in Subscribed, so
    // a client that wants to ask "am I in this team's group?" can build the name from an id it already
    // holds instead of re-encoding it.
    public static string Team(Guid teamId) => $"team:{teamId.ToBase58()}";
    public static string Issue(Guid issueId) => $"issue:{issueId.ToBase58()}";
    public static string User(Guid userId) => $"user:{userId.ToBase58()}";
    public const string Organization = "org";
}

public static class EntityTypes
{
    public const string Team = "team";
    public const string TeamMember = "teamMember";
    public const string WorkflowState = "workflowState";
    public const string Label = "label";
    public const string Project = "project";
    public const string Milestone = "milestone";
    public const string Document = "document";
    public const string Issue = "issue";
    public const string Comment = "comment";
    public const string Attachment = "attachment";
    public const string IssueRelation = "issueRelation";
    public const string User = "user";
    public const string ActivityEvent = "activityEvent";
    public const string Notification = "notification";
}
