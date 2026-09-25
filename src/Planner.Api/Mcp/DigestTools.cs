using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Planner.Api.Common;
using Planner.Contracts.Enums;
using Planner.Contracts.Realtime;

namespace Planner.Api.Mcp;

[McpServerToolType]
public sealed class DigestTools(McpReader reader)
{
    private const int ListLimit = 100;

    [McpServerTool(Name = "team_digest", Title = "Summarise a team's period", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Everything needed to answer 'what happened in <team> over <period>' in one call: issues completed, " +
        "created, started and canceled in the window; what is in progress now (and what has gone quiet); " +
        "overdue work; project and milestone progress; and who was active. Defaults to the last 7 days.")]
    public async Task<string> TeamDigestAsync(
        [Description("The team: its key, name or id.")] string team,
        [Description("Start of the period, as a date or moment (ISO 8601). A bare date is midnight in the user's time zone. Defaults to `days` ago.")] string? since = null,
        [Description("End of the period, exclusive (ISO 8601). Defaults to now.")] string? until = null,
        [Description("Length of the period in days, when `since` is not given.")] int days = 7,
        CancellationToken ct = default)
    {
        var found = await reader.ResolveTeamAsync(team, ct);
        var from = await reader.WindowStartAsync(since, days, 7, ct);
        var to = until is null ? DateTimeOffset.UtcNow : await reader.ParseMomentAsync(until, ct);

        if (to <= from)
        {
            throw new McpException("The end of the period has to come after its start.");
        }

        var db = reader.Db;
        var zone = await reader.TimeZoneAsync(ct);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
        var issues = db.Issues.AsNoTracking().Where(i => i.TeamId == found.Id);

        // Archived issues still count: work finished and then tidied away was still finished this week.
        var completedQuery = issues.Where(i =>
            i.CompletedAt >= from && i.CompletedAt < to && i.State.Type == WorkflowStateType.Completed);
        var createdQuery = issues.Where(i => i.CreatedAt >= from && i.CreatedAt < to);
        var startedQuery = issues.Where(i =>
            i.StartedAt >= from && i.StartedAt < to && i.State.Type == WorkflowStateType.Started);
        var canceledQuery = issues.Where(i =>
            i.CanceledAt >= from && i.CanceledAt < to && i.State.Type == WorkflowStateType.Canceled);
        var inProgressQuery = issues.Where(i => i.ArchivedAt == null && i.State.Type == WorkflowStateType.Started);
        var overdueQuery = issues.Where(i =>
            i.ArchivedAt == null && i.DueDate != null && i.DueDate < today &&
            i.State.Type != WorkflowStateType.Completed && i.State.Type != WorkflowStateType.Canceled);

        var completed = await ListAsync(completedQuery.OrderBy(i => i.CompletedAt), ct);
        var created = await ListAsync(createdQuery.OrderBy(i => i.CreatedAt), ct);
        var started = await ListAsync(startedQuery.OrderBy(i => i.StartedAt), ct);
        var canceled = await ListAsync(canceledQuery.OrderBy(i => i.CanceledAt), ct);
        var inProgress = await ListAsync(
            inProgressQuery.OrderBy(i => i.Priority == IssuePriority.None).ThenBy(i => i.Priority).ThenByDescending(i => i.UpdatedAt), ct);
        var overdue = await ListAsync(overdueQuery.OrderBy(i => i.DueDate), ct);

        var events = db.ActivityEvents.AsNoTracking()
            .Where(a => a.TeamId == found.Id && a.CreatedAt >= from && a.CreatedAt < to);

        var eventsByAction = await events
            .GroupBy(a => a.Action)
            .Select(g => new { Action = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var byActor = await events
            .GroupBy(a => a.Actor.DisplayName)
            .Select(g => new
            {
                Name = g.Key,
                Events = g.Count(),
                Comments = g.Count(a => a.Action == ActivityActions.Commented),
                Created = g.Count(a => a.Action == ActivityActions.Created && a.EntityType == EntityTypes.Issue)
            })
            .ToListAsync(ct);

        var completedBy = await completedQuery
            .GroupBy(i => i.Assignee != null ? i.Assignee.DisplayName : null)
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var contributors = byActor
            .Select(a => a.Name)
            .Union(completedBy.Where(c => c.Name != null).Select(c => c.Name!))
            .Select(name =>
            {
                var activity = byActor.FirstOrDefault(a => a.Name == name);
                return new
                {
                    name,
                    completedIssues = completedBy.FirstOrDefault(c => c.Name == name)?.Count ?? 0,
                    issuesCreated = activity?.Created ?? 0,
                    comments = activity?.Comments ?? 0,
                    changes = activity?.Events ?? 0
                };
            })
            .OrderByDescending(c => c.completedIssues)
            .ThenByDescending(c => c.changes)
            .ToList();

        // Projects that are running, or that anything happened to in the window.
        var touchedProjectIds = await events.Where(a => a.ProjectId != null).Select(a => a.ProjectId!.Value).Distinct().ToListAsync(ct);
        var projects = await db.Projects.AsNoTracking()
            .Where(p => p.TeamId == found.Id && p.ArchivedAt == null &&
                        (p.Status == ProjectStatus.InProgress || touchedProjectIds.Contains(p.Id) ||
                         (p.CompletedAt >= from && p.CompletedAt < to)))
            .OrderBy(p => p.Rank)
            .Select(Mapping.ProjectProjection)
            .ToListAsync(ct);

        var projectIds = projects.Select(p => p.Id).ToList();
        var completedPerProject = await completedQuery.Where(i => i.ProjectId != null && projectIds.Contains(i.ProjectId.Value))
            .GroupBy(i => i.ProjectId!.Value).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var createdPerProject = await createdQuery.Where(i => i.ProjectId != null && projectIds.Contains(i.ProjectId.Value))
            .GroupBy(i => i.ProjectId!.Value).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);

        var horizon = today.AddDays(14);
        var milestones = await db.Milestones.AsNoTracking()
            .Where(m => m.Project.TeamId == found.Id && m.Project.ArchivedAt == null &&
                        ((m.CompletedAt >= from && m.CompletedAt < to) ||
                         (m.Status != MilestoneStatus.Completed && m.TargetDate != null && m.TargetDate <= horizon)))
            .OrderBy(m => m.TargetDate)
            .Select(Mapping.MilestoneProjection)
            .ToListAsync(ct);

        var milestoneProjectIds = milestones.Select(m => m.ProjectId).Distinct().ToList();
        var milestoneProjects = await db.Projects.AsNoTracking()
            .Where(p => milestoneProjectIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name, ct);

        int Count(string action) => eventsByAction.FirstOrDefault(e => e.Action == action)?.Count ?? 0;

        return McpReader.Serialize(new
        {
            team = new { found.Key, found.Name },
            period = new
            {
                from = TimeZoneInfo.ConvertTime(from, zone),
                to = TimeZoneInfo.ConvertTime(to, zone),
                timeZone = zone.Id
            },
            totals = new
            {
                completed = completed.Total,
                created = created.Total,
                started = started.Total,
                canceled = canceled.Total,
                inProgressNow = inProgress.Total,
                overdueNow = overdue.Total,
                comments = Count(ActivityActions.Commented),
                stateChanges = Count(ActivityActions.StateChanged),
                changes = eventsByAction.Sum(e => e.Count)
            },
            completed = completed.Items,
            created = created.Items,
            started = started.Items,
            canceled = canceled.Items,
            inProgress = inProgress.Items.Select(i => new
            {
                issue = i,
                // Nothing recorded against it during the window: worth a question in the summary.
                quietThisPeriod = i.UpdatedAt < TimeZoneInfo.ConvertTime(from, zone)
            }),
            overdue = overdue.Items,
            projects = projects.Select(p => new
            {
                p.Name,
                p.Status,
                p.Health,
                Lead = p.Lead?.DisplayName,
                p.TargetDate,
                ProgressNow = p.Progress.ToView(),
                CompletedThisPeriod = completedPerProject.GetValueOrDefault(p.Id),
                CreatedThisPeriod = createdPerProject.GetValueOrDefault(p.Id),
                CompletedAt = p.CompletedAt is { } done ? TimeZoneInfo.ConvertTime(done, zone) : (DateTimeOffset?)null
            }),
            milestones = milestones.Select(m => new
            {
                Project = milestoneProjects.GetValueOrDefault(m.ProjectId),
                m.Name,
                m.Status,
                m.TargetDate,
                Progress = m.Progress.ToView(),
                CompletedThisPeriod = m.CompletedAt >= from && m.CompletedAt < to
            }),
            contributors,
            note = "Lists hold at most " + ListLimit + " issues each; totals are exact. Issue times are in the user's time zone."
        });

        async Task<Page<IssueView>> ListAsync(IQueryable<Domain.Entities.Issue> query, CancellationToken token)
        {
            var total = await query.LongCountAsync(token);
            var items = await query.Take(ListLimit).Select(IssueView.Projection).ToListAsync(token);
            return new Page<IssueView>(items.Select(i => i.In(zone)).ToList(), total, total > ListLimit);
        }
    }
}
