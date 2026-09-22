using Planner.Contracts.Auth;
using Planner.Contracts.Common;
using Planner.Contracts.Enums;

namespace Planner.Contracts.Projects;

public sealed record ProjectDto(
    Guid Id,
    Guid TeamId,
    string TeamKey,
    string Name,
    string? Summary,
    string? Description,
    ProjectStatus Status,
    ProjectHealth Health,
    string Color,
    UserSummary? Lead,
    DateOnly? StartDate,
    DateOnly? TargetDate,
    string Rank,
    ProjectProgress Progress,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? ArchivedAt);

/// <summary>Issue rollup for a project or milestone. Computed on read from issue workflow-state types
/// so it can never drift out of sync with the issues themselves.</summary>
public sealed record ProjectProgress(int Total, int Completed, int Started, int Canceled)
{
    /// <summary>Share of non-canceled work that is done, 0..1.</summary>
    public double Ratio => Total - Canceled <= 0 ? 0 : Math.Round(Completed / (double)(Total - Canceled), 4);
}

public sealed record CreateProjectRequest(
    Guid TeamId,
    string Name,
    string? Summary = null,
    string? Description = null,
    ProjectStatus Status = ProjectStatus.Backlog,
    ProjectHealth Health = ProjectHealth.OnTrack,
    string Color = "#6E79F1",
    Guid? LeadUserId = null,
    DateOnly? StartDate = null,
    DateOnly? TargetDate = null);

public sealed record UpdateProjectRequest(
    Optional<string> Name,
    Optional<string?> Summary,
    Optional<string?> Description,
    Optional<ProjectStatus> Status,
    Optional<ProjectHealth> Health,
    Optional<string> Color,
    Optional<Guid?> LeadUserId,
    Optional<DateOnly?> StartDate,
    Optional<DateOnly?> TargetDate,
    Optional<string> Rank);

public sealed record MilestoneDto(
    Guid Id,
    Guid ProjectId,
    string Name,
    string? Description,
    DateOnly? TargetDate,
    MilestoneStatus Status,
    string Rank,
    ProjectProgress Progress,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

public sealed record CreateMilestoneRequest(
    string Name,
    string? Description = null,
    DateOnly? TargetDate = null,
    MilestoneStatus Status = MilestoneStatus.Upcoming);

public sealed record UpdateMilestoneRequest(
    Optional<string> Name,
    Optional<string?> Description,
    Optional<DateOnly?> TargetDate,
    Optional<MilestoneStatus> Status,
    Optional<string> Rank);

public sealed record DocumentSummary(
    Guid Id,
    Guid TeamId,
    Guid? ProjectId,
    string Title,
    UserSummary CreatedBy,
    UserSummary? UpdatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt);

public sealed record DocumentDto(
    Guid Id,
    Guid TeamId,
    Guid? ProjectId,
    string Title,
    string Content,
    UserSummary CreatedBy,
    UserSummary? UpdatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt);

public sealed record CreateDocumentRequest(Guid TeamId, Guid? ProjectId, string Title, string Content = "");

public sealed record UpdateDocumentRequest(
    Optional<string> Title,
    Optional<string> Content,
    Optional<Guid?> ProjectId);
