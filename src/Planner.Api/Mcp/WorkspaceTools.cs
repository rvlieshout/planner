using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using Planner.Api.Common;
using Planner.Contracts.Common;
using Planner.Contracts.Enums;
using Planner.Contracts.Projects;

namespace Planner.Api.Mcp;

[McpServerToolType]
public sealed class WorkspaceTools(McpReader reader)
{
    [McpServerTool(Name = "list_teams", Title = "List teams", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The teams the user can see, with their key (the prefix of issue keys, e.g. DEV in DEV-42) and the user's role in each.")]
    public async Task<string> ListTeamsAsync(
        [Description("Include archived teams.")] bool includeArchived = false,
        CancellationToken ct = default)
    {
        var readable = await reader.ReadableTeamIdsAsync(ct);
        var userId = reader.UserId;

        var teams = await reader.Db.Teams.AsNoTracking()
            .Where(t => readable.Contains(t.Id) && (includeArchived || t.ArchivedAt == null))
            .OrderBy(t => t.Key)
            .Select(t => new TeamView(
                t.Key,
                t.Name,
                t.Description,
                t.IsPrivate,
                t.Members.Count,
                t.Members.Where(m => m.UserId == userId).Select(m => m.Role.ToString()).FirstOrDefault(),
                t.ArchivedAt != null))
            .ToListAsync(ct);

        return McpReader.Serialize(teams);
    }

    [McpServerTool(Name = "list_projects", Title = "List projects", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Projects the user can see, with status, health, lead, dates and issue progress (completed, started and canceled out of total).")]
    public async Task<string> ListProjectsAsync(
        [Description("Only this team: its key, name or id.")] string? team = null,
        [Description("Only projects in these statuses.")] ProjectStatus[]? status = null,
        [Description("Only projects whose name contains this text.")] string? search = null,
        [Description("Include archived projects.")] bool includeArchived = false,
        CancellationToken ct = default)
    {
        var readable = await reader.ReadableTeamIdsAsync(ct);
        var query = reader.Db.Projects.AsNoTracking().Where(p => readable.Contains(p.TeamId));

        if (team is not null)
        {
            var teamId = (await reader.ResolveTeamAsync(team, ct)).Id;
            query = query.Where(p => p.TeamId == teamId);
        }

        if (status is { Length: > 0 })
        {
            query = query.Where(p => status.Contains(p.Status));
        }

        if (!includeArchived)
        {
            query = query.Where(p => p.ArchivedAt == null);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(p => EF.Functions.ILike(p.Name, pattern));
        }

        var projects = await query
            .OrderBy(p => p.Team.Key)
            .ThenBy(p => p.Rank)
            .Take(200)
            .Select(Mapping.ProjectProjection)
            .ToListAsync(ct);

        var zone = await reader.TimeZoneAsync(ct);
        return McpReader.Serialize(projects.Select(p => ToView(p, zone)).ToList());
    }

    [McpServerTool(Name = "get_project", Title = "Get a project", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("One project in full: its brief, milestones with their own progress, and the issues currently in progress.")]
    public async Task<string> GetProjectAsync(
        [Description("The project's name (or a unique part of it) or id.")] string project,
        [Description("The team it belongs to, when the name alone is ambiguous.")] string? team = null,
        CancellationToken ct = default)
    {
        var teamId = team is null ? (Guid?)null : (await reader.ResolveTeamAsync(team, ct)).Id;
        var found = await reader.ResolveProjectAsync(project, teamId, ct);
        var zone = await reader.TimeZoneAsync(ct);

        var dto = await reader.Db.Projects.AsNoTracking()
            .Where(p => p.Id == found.Id)
            .Select(Mapping.ProjectProjection)
            .SingleAsync(ct);

        var milestones = await reader.Db.Milestones.AsNoTracking()
            .Where(m => m.ProjectId == found.Id)
            .OrderBy(m => m.Rank)
            .Select(Mapping.MilestoneProjection)
            .ToListAsync(ct);

        var inProgress = await reader.Db.Issues.AsNoTracking()
            .Where(i => i.ProjectId == found.Id && i.ArchivedAt == null && i.State.Type == WorkflowStateType.Started)
            .OrderByDescending(i => i.UpdatedAt)
            .Take(50)
            .Select(IssueView.Projection)
            .ToListAsync(ct);

        return McpReader.Serialize(new
        {
            project = ToView(dto, zone),
            description = dto.Description,
            milestones = milestones.Select(m => new
            {
                m.Name,
                m.Description,
                m.Status,
                m.TargetDate,
                Progress = m.Progress.ToView()
            }),
            inProgress = inProgress.Select(i => i.In(zone))
        });
    }

    private static ProjectView ToView(ProjectDto p, TimeZoneInfo zone) => new(
        p.Id.ToBase58(),
        p.Name,
        p.TeamKey,
        p.Summary,
        p.Status,
        p.Health,
        p.Lead?.DisplayName,
        p.StartDate,
        p.TargetDate,
        p.Progress.ToView(),
        TimeZoneInfo.ConvertTime(p.UpdatedAt, zone),
        p.CompletedAt is { } done ? TimeZoneInfo.ConvertTime(done, zone) : null,
        p.ArchivedAt is not null);
}
