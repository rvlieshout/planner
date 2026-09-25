using System.Linq.Expressions;
using System.Text.Json;
using Planner.Contracts.Enums;
using Planner.Domain.Entities;

namespace Planner.Api.Mcp;

// What the tools answer with. Deliberately not the REST DTOs: those carry what a UI needs (colours,
// ranks, ids for every link), and an assistant pays for every one of those tokens on every call. These
// name things the way a person would — DEV-42, "Development", "Ada Lovelace" — and leave the rest out.

public sealed record TeamView(
    string Key,
    string Name,
    string? Description,
    bool IsPrivate,
    int MemberCount,
    string? YourRole,
    bool Archived);

public sealed record ProgressView(int Total, int Completed, int Started, int Canceled, int PercentDone);

public sealed record ProjectView(
    string Id,
    string Name,
    string Team,
    string? Summary,
    ProjectStatus Status,
    ProjectHealth Health,
    string? Lead,
    DateOnly? StartDate,
    DateOnly? TargetDate,
    ProgressView Progress,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    bool Archived);

public sealed record IssueView(
    string Key,
    string Title,
    string State,
    WorkflowStateType StateType,
    IssuePriority Priority,
    string? Assignee,
    string? Project,
    string? Milestone,
    IReadOnlyList<string>? Labels,
    int? Estimate,
    DateOnly? DueDate,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? CanceledAt,
    bool? Archived)
{
    /// <summary>One query for everything above; the state, people and labels are joined in SQL.</summary>
    public static readonly Expression<Func<Issue, IssueView>> Projection = i => new IssueView(
        i.Team.Key + "-" + i.Number,
        i.Title,
        i.State.Name,
        i.State.Type,
        i.Priority,
        i.Assignee != null ? i.Assignee.DisplayName : null,
        i.Project != null ? i.Project.Name : null,
        i.Milestone != null ? i.Milestone.Name : null,
        i.Labels.Select(l => l.Label.Name).ToList(),
        i.Estimate,
        i.DueDate,
        i.CreatedAt,
        i.UpdatedAt,
        i.StartedAt,
        i.CompletedAt,
        i.CanceledAt,
        i.ArchivedAt != null);

    /// <summary>Drops what is usually empty, so a model reads a dozen fields rather than twenty.</summary>
    public IssueView Trimmed() => this with
    {
        Labels = Labels is { Count: > 0 } ? Labels : null,
        Archived = Archived == true ? true : null
    };
}

/// <param name="ReplyTo">The id of the comment this answers.</param>
/// <param name="Mine">Written by the user, so update_comment can edit it.</param>
public sealed record CommentView(
    string Id, string Author, DateTimeOffset At, string Body, string? ReplyTo, bool? Mine, bool? Edited);

public sealed record RelationView(string Type, string Key, string Title, string State);

public sealed record ActivityView(
    DateTimeOffset At,
    string Actor,
    string Action,
    string Entity,
    string? Team,
    string? Project,
    string? Issue,
    string? IssueTitle,
    JsonElement? Change);

public sealed record Page<T>(IReadOnlyList<T> Items, long Total, bool More);

internal static class McpViewTimes
{
    /// <summary>Moves every timestamp of an issue into the user's zone, so the model never has to.</summary>
    public static IssueView In(this IssueView issue, TimeZoneInfo zone) => issue.Trimmed() with
    {
        CreatedAt = TimeZoneInfo.ConvertTime(issue.CreatedAt, zone),
        UpdatedAt = TimeZoneInfo.ConvertTime(issue.UpdatedAt, zone),
        StartedAt = issue.StartedAt is { } s ? TimeZoneInfo.ConvertTime(s, zone) : null,
        CompletedAt = issue.CompletedAt is { } c ? TimeZoneInfo.ConvertTime(c, zone) : null,
        CanceledAt = issue.CanceledAt is { } x ? TimeZoneInfo.ConvertTime(x, zone) : null
    };

    public static ProgressView ToView(this Contracts.Projects.ProjectProgress progress) =>
        new(progress.Total, progress.Completed, progress.Started, progress.Canceled,
            (int)Math.Round(progress.Ratio * 100));
}
