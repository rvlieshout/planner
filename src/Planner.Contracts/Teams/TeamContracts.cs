using Planner.Contracts.Common;
using Planner.Domain.Enums;

namespace Planner.Contracts.Teams;

public sealed record TeamDto(
    Guid Id,
    string Key,
    string Name,
    string? Description,
    string Color,
    bool IsPrivate,
    int MemberCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt);

public sealed record CreateTeamRequest(
    string Key,
    string Name,
    string? Description = null,
    string Color = "#6E79F1",
    bool IsPrivate = false);

public sealed record UpdateTeamRequest(
    Optional<string> Name,
    Optional<string?> Description,
    Optional<string> Color,
    Optional<bool> IsPrivate);

public sealed record TeamMemberDto(
    Guid TeamId,
    Guid UserId,
    string DisplayName,
    string Email,
    string? AvatarUrl,
    TeamRole Role,
    DateTimeOffset CreatedAt);

public sealed record AddTeamMemberRequest(Guid UserId, TeamRole Role = TeamRole.Member);

public sealed record UpdateTeamMemberRequest(TeamRole Role);

public sealed record WorkflowStateDto(
    Guid Id,
    Guid TeamId,
    string Name,
    WorkflowStateType Type,
    string Color,
    int Position,
    bool IsDefault);

public sealed record CreateWorkflowStateRequest(
    string Name,
    WorkflowStateType Type,
    string Color = "#95A2B3",
    int? Position = null,
    bool IsDefault = false);

public sealed record UpdateWorkflowStateRequest(
    Optional<string> Name,
    Optional<WorkflowStateType> Type,
    Optional<string> Color,
    Optional<int> Position,
    Optional<bool> IsDefault);

public sealed record LabelDto(Guid Id, Guid? TeamId, string Name, string Color, string? Description);

public sealed record CreateLabelRequest(string Name, string Color = "#95A2B3", string? Description = null);

public sealed record UpdateLabelRequest(
    Optional<string> Name,
    Optional<string> Color,
    Optional<string?> Description);
