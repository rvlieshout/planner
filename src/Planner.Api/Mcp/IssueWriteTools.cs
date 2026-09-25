using System.ComponentModel;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Planner.Api.Auth;
using Planner.Api.Endpoints;
using Planner.Contracts.Enums;
using Planner.Contracts.Issues;
using Planner.Domain.Entities;

namespace Planner.Api.Mcp;

/// <summary>The tools that change things. Each goes through the same code as the REST API, so the user's
/// team permissions, validation, numbering, the activity log and live updates in open browsers all apply
/// exactly as if they had made the change themselves. Which, as far as Planner is concerned, they did:
/// the issue is theirs, and the history says so.</summary>
[McpServerToolType]
public sealed class IssueWriteTools(
    McpReader reader,
    IssueCreator creator,
    IHttpContextAccessor http,
    IOptions<PlannerAuthOptions> auth)
{
    /// <summary>How far back an identical issue by the same person counts as the same request made twice.</summary>
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromMinutes(10);

    [McpServerTool(Name = "create_issue", Title = "Create an issue", ReadOnly = false, Destructive = false,
        Idempotent = false, OpenWorld = false)]
    [Description(
        "Create an issue in a team, as the user. Everything but team and title is optional; give names as a " +
        "person would (state 'In Review', project 'Ventura Greenhouse', labels ['Bug'], assignee 'me'). New " +
        "issues land in the team's default state unless one is given. Returns the new issue with its key and " +
        "a link. An identical title from the same user in the same team within 10 minutes is treated as a " +
        "retry and returns the existing issue instead, unless allowDuplicate is set.")]
    public async Task<string> CreateIssueAsync(
        [Description("The team: its key, name or id. list_teams shows which ones the user can create in.")] string team,
        [Description("A short summary of the work, up to 500 characters.")] string title,
        [Description("Details in Markdown: context, steps to reproduce, acceptance criteria.")] string? description = null,
        [Description("A workflow state by name ('Todo', 'In Review') or type ('backlog', 'unstarted', 'started'). Defaults to the team's default state.")] string? state = null,
        [Description("How urgent it is.")] IssuePriority priority = IssuePriority.None,
        [Description("Who should do it: 'me', an email address or a display name. Must be a member of the team.")] string? assignee = null,
        [Description("The project it belongs to, by name or id.")] string? project = null,
        [Description("A milestone of that project, by name. Needs `project`.")] string? milestone = null,
        [Description("The key of a parent issue in the same team, to make this a sub-issue, e.g. DEV-12.")] string? parent = null,
        [Description("Labels by name, from the team's labels or the organisation's.")] string[]? labels = null,
        [Description("Estimate, in whatever unit the team uses (points or hours), 0 to 1000.")] int? estimate = null,
        [Description("Due date, as an ISO date (2026-10-15).")] string? dueDate = null,
        [Description("Create it even when an identical issue was just created; only for deliberate duplicates.")] bool allowDuplicate = false,
        CancellationToken ct = default)
    {
        var db = reader.Db;
        var found = await reader.ResolveTeamAsync(team, ct);

        Guid? projectId = project is null ? null : (await reader.ResolveProjectAsync(project, found.Id, ct)).Id;

        if (milestone is not null && projectId is null)
        {
            throw new McpException("A milestone belongs to a project: pass `project` as well.");
        }

        var request = new CreateIssueRequest(
            TeamId: found.Id,
            Title: title,
            Description: string.IsNullOrWhiteSpace(description) ? null : description,
            StateId: state is null ? null : await ResolveStateAsync(found.Id, state, ct),
            Priority: priority,
            AssigneeId: assignee is null ? null : await reader.ResolveUserAsync(assignee, ct),
            ProjectId: projectId,
            MilestoneId: milestone is null ? null : await ResolveMilestoneAsync(projectId!.Value, milestone, ct),
            ParentId: parent is null ? null : (await reader.ResolveIssueAsync(parent, ct)).Id,
            Estimate: estimate,
            DueDate: dueDate is null ? null : ParseDate(dueDate),
            LabelIds: labels is { Length: > 0 } ? await ResolveLabelsAsync(found.Id, labels, ct) : null);

        // Assistants retry: after a timeout, or when they lose track of what they already did. A second
        // identical issue is noise someone has to clean up, so a repeat within a few minutes is answered
        // with the first one.
        if (!allowDuplicate && !string.IsNullOrWhiteSpace(title))
        {
            var normalized = title.Trim().ToUpper();
            var since = DateTimeOffset.UtcNow - DuplicateWindow;
            var existing = await db.Issues.AsNoTracking()
                .Where(i => i.TeamId == found.Id && i.CreatorId == reader.UserId && i.CreatedAt >= since &&
                            i.Title.ToUpper() == normalized)
                .OrderByDescending(i => i.CreatedAt)
                .Select(IssueView.Projection)
                .FirstOrDefaultAsync(ct);

            if (existing is not null)
            {
                return await ResultAsync(existing, created: false, ct,
                    note: "You created an issue with this title in this team a few minutes ago, so no new one was made. " +
                          "Pass allowDuplicate: true if a second one is really wanted.");
            }
        }

        var outcome = await creator.CreateAsync(request, ct);

        if (outcome.Issue is null)
        {
            throw new McpException(outcome.ErrorMessage ?? "The issue could not be created.");
        }

        var view = await db.Issues.AsNoTracking()
            .Where(i => i.Id == outcome.Issue.Id)
            .Select(IssueView.Projection)
            .SingleAsync(ct);

        return await ResultAsync(view, created: true, ct);
    }

    private async Task<string> ResultAsync(IssueView issue, bool created, CancellationToken ct, string? note = null)
    {
        var zone = await reader.TimeZoneAsync(ct);
        return McpReader.Serialize(new { created, issue = issue.In(zone), url = WebUrl($"app/issues/{issue.Key}"), note });
    }

    /// <summary>A link the user can open. From the configured public address when there is one, so it is
    /// right behind a proxy, and from the request otherwise.</summary>
    private string? WebUrl(string path)
    {
        var configured = auth.Value.Issuer;

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.TrimEnd('/') + "/" + path;
        }

        var request = http.HttpContext?.Request;
        return request is null ? null : $"{request.Scheme}://{request.Host}{request.PathBase}/{path}";
    }

    /// <summary>A state by name ("In Review"), or by type ("started"), which picks the first such column on
    /// the board. Unknown names are answered with the team's states.</summary>
    private async Task<Guid> ResolveStateAsync(Guid teamId, string state, CancellationToken ct)
    {
        var states = await reader.Db.WorkflowStates.AsNoTracking()
            .Where(s => s.TeamId == teamId)
            .OrderBy(s => s.Rank)
            .ToListAsync(ct);
        var wanted = state.Trim();

        var match = states.FirstOrDefault(s => string.Equals(s.Name, wanted, StringComparison.OrdinalIgnoreCase))
                    ?? (Enum.TryParse<WorkflowStateType>(wanted, ignoreCase: true, out var type) && !int.TryParse(wanted, out _)
                        ? states.FirstOrDefault(s => s.Type == type)
                        : null);

        return match?.Id ?? throw new McpException(
            $"No state '{wanted}' in this team. Its states are: " +
            string.Join(", ", states.Select(s => $"{s.Name} ({s.Type.ToString().ToLowerInvariant()})")));
    }

    private async Task<Guid> ResolveMilestoneAsync(Guid projectId, string milestone, CancellationToken ct)
    {
        var milestones = await reader.Db.Milestones.AsNoTracking()
            .Where(m => m.ProjectId == projectId)
            .OrderBy(m => m.Rank)
            .ToListAsync(ct);
        var wanted = milestone.Trim();

        var match = milestones.FirstOrDefault(m => string.Equals(m.Name, wanted, StringComparison.OrdinalIgnoreCase));

        return match?.Id ?? throw new McpException(milestones.Count == 0
            ? "That project has no milestones."
            : $"No milestone '{wanted}' in that project. Its milestones are: {string.Join(", ", milestones.Select(m => m.Name))}");
    }

    /// <summary>The team's own labels win over an organisation label of the same name.</summary>
    private async Task<IReadOnlyList<Guid>> ResolveLabelsAsync(Guid teamId, string[] names, CancellationToken ct)
    {
        var usable = await reader.Db.Labels.AsNoTracking()
            .Where(l => l.TeamId == null || l.TeamId == teamId)
            .OrderBy(l => l.TeamId == null)
            .ToListAsync(ct);

        var ids = new List<Guid>();
        var unknown = new List<string>();

        foreach (var name in names.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (usable.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)) is Label label)
            {
                ids.Add(label.Id);
            }
            else
            {
                unknown.Add(name);
            }
        }

        if (unknown.Count > 0)
        {
            throw new McpException(
                $"No label named {string.Join(", ", unknown.Select(n => $"'{n}'"))} in this team. Labels you can use: " +
                (usable.Count == 0 ? "none." : string.Join(", ", usable.Select(l => l.Name).Distinct())));
        }

        return ids.Distinct().ToList();
    }

    private static DateOnly ParseDate(string value) =>
        DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new McpException($"'{value}' is not a date. Use an ISO date such as 2026-10-15.");
}
