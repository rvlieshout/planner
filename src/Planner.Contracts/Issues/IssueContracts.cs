using Planner.Contracts.Auth;
using Planner.Contracts.Common;
using Planner.Contracts.Teams;
using Planner.Contracts.Enums;

namespace Planner.Contracts.Issues;

/// <summary>Board/list row. Deliberately flat and small — a board view fetches hundreds of these.</summary>
public sealed record IssueSummary(
    Guid Id,
    string Key,
    Guid TeamId,
    int Number,
    string Title,
    Guid StateId,
    string StateName,
    WorkflowStateType StateType,
    string StateColor,
    IssuePriority Priority,
    UserSummary? Assignee,
    Guid? ProjectId,
    Guid? MilestoneId,
    Guid? ParentId,
    int? Estimate,
    DateOnly? DueDate,
    string Rank,
    IReadOnlyList<LabelDto> Labels,
    int SubIssueCount,
    int CommentCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? ArchivedAt);

public sealed record IssueDetail(
    Guid Id,
    string Key,
    Guid TeamId,
    int Number,
    string Title,
    string? Description,
    Guid StateId,
    string StateName,
    WorkflowStateType StateType,
    string StateColor,
    IssuePriority Priority,
    UserSummary? Assignee,
    UserSummary Creator,
    Guid? ProjectId,
    string? ProjectName,
    Guid? MilestoneId,
    string? MilestoneName,
    Guid? ParentId,
    string? ParentKey,
    int? Estimate,
    DateOnly? DueDate,
    string Rank,
    IReadOnlyList<LabelDto> Labels,
    IReadOnlyList<IssueSummary> Children,
    IReadOnlyList<IssueRelationDto> Relations,
    IReadOnlyList<AttachmentDto> Attachments,
    int CommentCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? CanceledAt,
    DateTimeOffset? ArchivedAt);

public sealed record CreateIssueRequest(
    Guid TeamId,
    string Title,
    string? Description = null,
    Guid? StateId = null,
    IssuePriority Priority = IssuePriority.None,
    Guid? AssigneeId = null,
    Guid? ProjectId = null,
    Guid? MilestoneId = null,
    Guid? ParentId = null,
    int? Estimate = null,
    DateOnly? DueDate = null,
    IReadOnlyList<Guid>? LabelIds = null);

public sealed record UpdateIssueRequest(
    Optional<string> Title,
    Optional<string?> Description,
    Optional<Guid> StateId,
    Optional<IssuePriority> Priority,
    Optional<Guid?> AssigneeId,
    Optional<Guid?> ProjectId,
    Optional<Guid?> MilestoneId,
    Optional<Guid?> ParentId,
    Optional<int?> Estimate,
    Optional<DateOnly?> DueDate,
    Optional<string> Rank,
    Optional<IReadOnlyList<Guid>> LabelIds);

/// <summary>Drag-and-drop on a board: change column and/or rank in one atomic call. The anchors are the
/// issues it was dropped between; the server takes a <c>Rank</c> key between theirs. An explicit
/// <c>Rank</c> is used only when neither anchor is given.</summary>
public sealed record MoveIssueRequest(Guid? StateId, string? Rank, Guid? AfterIssueId, Guid? BeforeIssueId);

/// <summary>Issue list filter. Every field is optional and AND-combined; repeated query parameters
/// (e.g. <c>?state=..&amp;state=..</c>) become OR sets within that field.</summary>
public sealed record IssueFilter(
    Guid? TeamId = null,
    Guid? ProjectId = null,
    Guid? MilestoneId = null,
    Guid? ParentId = null,
    Guid[]? StateId = null,
    WorkflowStateType[]? StateType = null,
    Guid[]? AssigneeId = null,
    Guid[]? LabelId = null,
    IssuePriority[]? Priority = null,
    bool? Unassigned = null,
    bool IncludeArchived = false,
    bool TopLevelOnly = false,
    string? Search = null,
    DateOnly? DueBefore = null,
    DateOnly? DueAfter = null,
    DateTimeOffset? UpdatedSince = null,
    string Sort = "-updatedAt");

public sealed record IssueRelationDto(
    Guid Id,
    IssueRelationType Type,
    // IsOutgoing is false when the row was stored on the other issue and is shown inverted here.
    bool IsOutgoing,
    Guid IssueId,
    string IssueKey,
    string IssueTitle,
    WorkflowStateType StateType);

public sealed record CreateIssueRelationRequest(Guid TargetIssueId, IssueRelationType Type);

public sealed record CommentDto(
    Guid Id,
    Guid IssueId,
    UserSummary Author,
    string Body,
    Guid? ParentCommentId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? EditedAt);

public sealed record CreateCommentRequest(string Body, Guid? ParentCommentId = null);

public sealed record UpdateCommentRequest(string Body);

public sealed record AttachmentDto(
    Guid Id,
    Guid IssueId,
    string FileName,
    string? ContentType,
    long? SizeBytes,
    string StorageUri,
    UserSummary UploadedBy,
    DateTimeOffset CreatedAt);

public sealed record CreateAttachmentRequest(
    string FileName,
    string StorageUri,
    string? ContentType = null,
    long? SizeBytes = null);

public sealed record ActivityEventDto(
    Guid Id,
    string EntityType,
    Guid EntityId,
    Guid? TeamId,
    Guid? ProjectId,
    Guid? IssueId,
    UserSummary Actor,
    string Action,
    System.Text.Json.JsonElement? Data,
    DateTimeOffset CreatedAt);
