using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Planner.Api.Endpoints;
using Planner.Contracts.Common;
using Planner.Contracts.Enums;
using Planner.Contracts.Issues;

namespace Planner.Api.Mcp;

/// <summary>The tools that change things. Each goes through the same code as the REST API, so the user's
/// team permissions, validation, numbering, the activity log and live updates in open browsers all apply
/// exactly as if they had made the change themselves. Which, as far as Planner is concerned, they did:
/// the issue is theirs, and the history says so.</summary>
[McpServerToolType]
public sealed class IssueWriteTools(McpReader reader, IssueCommands issues)
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
            StateId: state is null ? null : await reader.ResolveStateAsync(found.Id, state, ct),
            Priority: priority,
            AssigneeId: assignee is null ? null : await reader.ResolveUserAsync(assignee, ct),
            ProjectId: projectId,
            MilestoneId: milestone is null ? null : await reader.ResolveMilestoneAsync(projectId!.Value, milestone, ct),
            ParentId: parent is null ? null : (await reader.ResolveIssueAsync(parent, ct)).Id,
            Estimate: estimate,
            DueDate: dueDate is null ? null : McpReader.ParseDate(dueDate),
            LabelIds: labels is { Length: > 0 } ? await reader.ResolveLabelsAsync(found.Id, labels, ct) : null);

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

        var outcome = await issues.CreateAsync(request, ct);

        if (outcome.Value is null)
        {
            throw new McpException(outcome.ErrorMessage ?? "The issue could not be created.");
        }

        var view = await db.Issues.AsNoTracking()
            .Where(i => i.Id == outcome.Value.Id)
            .Select(IssueView.Projection)
            .SingleAsync(ct);

        return await ResultAsync(view, created: true, ct);
    }

    /// <summary>Fields update_issue can clear. Clearing is explicit because an MCP argument that is left out
    /// and one sent as null look the same once they arrive.</summary>
    private static readonly string[] Clearable =
        ["assignee", "project", "milestone", "parent", "estimate", "dueDate", "description", "labels"];

    [McpServerTool(Name = "update_issue", Title = "Edit an issue", ReadOnly = false, Destructive = false,
        Idempotent = true, OpenWorld = false)]
    [Description(
        "Change an issue's fields, as the user. Only what is passed changes. Names work as in create_issue. " +
        "To empty a field, list it in `clear`. To change the description, prefer appendToDescription (adds a " +
        "paragraph); replacing it with `description` needs the `version` from get_issue, and is refused if " +
        "someone changed the issue since. To move an issue across the board, use move_issue.")]
    public async Task<string> UpdateIssueAsync(
        [Description("The issue key, e.g. DEV-42.")] string key,
        [Description("A new title.")] string? title = null,
        [Description("A new description replacing the old one entirely, in Markdown. Needs `version`.")] string? description = null,
        [Description("Markdown added to the end of the description as a new paragraph. Needs no version.")] string? appendToDescription = null,
        [Description("The issue's `version` from get_issue. Required to replace or clear the description.")] string? version = null,
        [Description("A new workflow state by name or type. Prefer move_issue, which also places it in the column.")] string? state = null,
        [Description("A new priority.")] IssuePriority? priority = null,
        [Description("A new assignee: 'me', an email address or a display name.")] string? assignee = null,
        [Description("Move it to this project, by name or id.")] string? project = null,
        [Description("A milestone of the issue's project (or of `project`, when given), by name.")] string? milestone = null,
        [Description("Make it a sub-issue of this issue key, in the same team.")] string? parent = null,
        [Description("Replace all labels with these, by name.")] string[]? labels = null,
        [Description("Labels to add, by name.")] string[]? addLabels = null,
        [Description("Labels to remove, by name.")] string[]? removeLabels = null,
        [Description("A new estimate, 0 to 1000.")] int? estimate = null,
        [Description("A new due date, as an ISO date.")] string? dueDate = null,
        [Description("Fields to empty: assignee, project, milestone, parent, estimate, dueDate, description, labels.")] string[]? clear = null,
        CancellationToken ct = default)
    {
        var issue = await reader.ResolveIssueAsync(key, ct);
        var teamId = issue.TeamId;
        var cleared = new HashSet<string>(clear ?? [], StringComparer.OrdinalIgnoreCase);

        if (cleared.FirstOrDefault(c => !Clearable.Contains(c, StringComparer.OrdinalIgnoreCase)) is { } unknown)
        {
            throw new McpException($"'{unknown}' cannot be cleared. Clearable fields: {string.Join(", ", Clearable)}.");
        }

        void Conflict(string field, bool set)
        {
            if (set && cleared.Contains(field))
            {
                throw new McpException($"`{field}` is both set and listed in `clear`; do one or the other.");
            }
        }

        Conflict("assignee", assignee is not null);
        Conflict("project", project is not null);
        Conflict("milestone", milestone is not null);
        Conflict("parent", parent is not null);
        Conflict("estimate", estimate is not null);
        Conflict("dueDate", dueDate is not null);
        Conflict("description", description is not null || appendToDescription is not null);
        Conflict("labels", labels is not null || addLabels is not null || removeLabels is not null);

        if (description is not null && appendToDescription is not null)
        {
            throw new McpException("Pass either `description` (replace) or `appendToDescription` (add), not both.");
        }

        if (labels is not null && (addLabels is not null || removeLabels is not null))
        {
            throw new McpException("Pass either `labels` (replace all) or addLabels/removeLabels, not both.");
        }

        // Replacing or clearing the text is the one edit that can erase someone else's work.
        if (description is not null || cleared.Contains("description"))
        {
            McpReader.RequireCurrent(version, issue.UpdatedAt, "issue's description");
        }

        Guid? projectId = project is null ? null : (await reader.ResolveProjectAsync(project, teamId, ct)).Id;
        var milestoneProject = projectId ?? (cleared.Contains("project") ? null : issue.ProjectId);

        if (milestone is not null && milestoneProject is null)
        {
            throw new McpException("The issue is not in a project, so it has no milestones. Pass `project` as well.");
        }

        Optional<IReadOnlyList<Guid>> labelIds = default;
        if (labels is not null)
        {
            labelIds = Optional<IReadOnlyList<Guid>>.From(await reader.ResolveLabelsAsync(teamId, labels, ct));
        }
        else if (addLabels is not null || removeLabels is not null)
        {
            var current = await reader.Db.IssueLabels.Where(l => l.IssueId == issue.Id).Select(l => l.LabelId).ToListAsync(ct);
            var add = addLabels is { Length: > 0 } ? await reader.ResolveLabelsAsync(teamId, addLabels, ct) : [];
            var remove = removeLabels is { Length: > 0 } ? await reader.ResolveLabelsAsync(teamId, removeLabels, ct) : [];
            labelIds = Optional<IReadOnlyList<Guid>>.From(current.Union(add).Except(remove).ToList());
        }
        else if (cleared.Contains("labels"))
        {
            labelIds = Optional<IReadOnlyList<Guid>>.From([]);
        }

        static Optional<T> Set<T>(bool set, T value) => set ? Optional<T>.From(value) : default;

        var request = new UpdateIssueRequest(
            Title: Set(title is not null, title!),
            Description: description is not null ? Optional<string?>.From(description)
                : appendToDescription is not null ? Optional<string?>.From(McpReader.Append(issue.Description, appendToDescription))
                : Set<string?>(cleared.Contains("description"), null),
            StateId: state is null ? default : Optional<Guid>.From(await reader.ResolveStateAsync(teamId, state, ct)),
            Priority: Set(priority is not null, priority.GetValueOrDefault()),
            AssigneeId: assignee is not null ? Optional<Guid?>.From(await reader.ResolveUserAsync(assignee, ct)) : Set<Guid?>(cleared.Contains("assignee"), null),
            ProjectId: projectId is not null ? Optional<Guid?>.From(projectId) : Set<Guid?>(cleared.Contains("project"), null),
            MilestoneId: milestone is not null ? Optional<Guid?>.From(await reader.ResolveMilestoneAsync(milestoneProject!.Value, milestone, ct))
                : Set<Guid?>(cleared.Contains("milestone"), null),
            ParentId: parent is not null ? Optional<Guid?>.From((await reader.ResolveIssueAsync(parent, ct)).Id) : Set<Guid?>(cleared.Contains("parent"), null),
            Estimate: estimate is not null ? Optional<int?>.From(estimate) : Set<int?>(cleared.Contains("estimate"), null),
            DueDate: dueDate is not null ? Optional<DateOnly?>.From(McpReader.ParseDate(dueDate)) : Set<DateOnly?>(cleared.Contains("dueDate"), null),
            Rank: default,
            LabelIds: labelIds);

        if (!request.Title.IsSet && !request.Description.IsSet && !request.StateId.IsSet && !request.Priority.IsSet &&
            !request.AssigneeId.IsSet && !request.ProjectId.IsSet && !request.MilestoneId.IsSet && !request.ParentId.IsSet &&
            !request.Estimate.IsSet && !request.DueDate.IsSet && !request.LabelIds.IsSet)
        {
            throw new McpException("Nothing to change: pass at least one field, or list fields in `clear`.");
        }

        var outcome = await issues.UpdateAsync(issue.Id, request, ct);

        return outcome.Value is null
            ? throw new McpException(outcome.ErrorMessage ?? "The issue could not be updated.")
            : await ChangedAsync(issue.Id, ct);
    }

    [McpServerTool(Name = "move_issue", Title = "Move an issue on the board", ReadOnly = false, Destructive = false,
        Idempotent = true, OpenWorld = false)]
    [Description(
        "Move an issue to another workflow state (board column) and/or place it within its column, as the user " +
        "would by dragging it. Moving to a completed or canceled state closes it; moving back reopens it. " +
        "Without a position it goes to the bottom of the column.")]
    public async Task<string> MoveIssueAsync(
        [Description("The issue key, e.g. DEV-42.")] string key,
        [Description("The state to move to, by name ('In Review') or type ('started', 'completed'). Leave out to stay in the same column.")] string? state = null,
        [Description("'top' or 'bottom' of the column.")] string? position = null,
        [Description("Place it directly after (below) this issue key, which must be in the target column.")] string? after = null,
        [Description("Place it directly before (above) this issue key, which must be in the target column.")] string? before = null,
        CancellationToken ct = default)
    {
        var issue = await reader.ResolveIssueAsync(key, ct);
        var db = reader.Db;

        if (new[] { position, after, before }.Count(p => p is not null) > 1)
        {
            throw new McpException("Pass one of position, after or before.");
        }

        if (state is null && position is null && after is null && before is null)
        {
            throw new McpException("Nothing to move: pass a state, a position, or an issue to place it next to.");
        }

        var stateId = state is null ? issue.StateId : await reader.ResolveStateAsync(issue.TeamId, state, ct);
        var column = db.Issues.AsNoTracking()
            .Where(i => i.TeamId == issue.TeamId && i.StateId == stateId && i.Id != issue.Id && i.ArchivedAt == null);

        async Task<Guid> AnchorAsync(string anchorKey)
        {
            var anchor = await reader.ResolveIssueAsync(anchorKey, ct);
            return await column.AnyAsync(i => i.Id == anchor.Id, ct)
                ? anchor.Id
                : throw new McpException($"{anchorKey.ToUpperInvariant()} is not in the column the issue is moving to.");
        }

        Guid? afterId = after is null ? null : await AnchorAsync(after);
        Guid? beforeId = before is null ? null : await AnchorAsync(before);

        switch (position?.Trim().ToLowerInvariant())
        {
            case null or "bottom":
                break;
            case "top":
                // Above whatever is first now; an empty column needs no anchor.
                beforeId = await column.OrderBy(i => i.Rank).ThenBy(i => i.Number).Select(i => (Guid?)i.Id).FirstOrDefaultAsync(ct);
                break;
            default:
                throw new McpException("position is 'top' or 'bottom'.");
        }

        var outcome = await issues.MoveAsync(issue.Id,
            new MoveIssueRequest(StateId: state is null ? null : stateId, Rank: null, AfterIssueId: afterId, BeforeIssueId: beforeId), ct);

        if (outcome.Value is null)
        {
            throw new McpException(outcome.ErrorMessage ?? "The issue could not be moved.");
        }

        // Where it landed, as a person would read the board: third of seven in In Review.
        var moved = await db.Issues.AsNoTracking().Where(i => i.Id == issue.Id).Select(i => new { i.Rank, i.Number, i.StateId }).SingleAsync(ct);
        var inColumn = db.Issues.AsNoTracking().Where(i => i.TeamId == issue.TeamId && i.StateId == moved.StateId && i.ArchivedAt == null);
        var place = await inColumn.CountAsync(i => string.Compare(i.Rank, moved.Rank) < 0 ||
                                                   (i.Rank == moved.Rank && i.Number < moved.Number), ct) + 1;
        var of = await inColumn.CountAsync(ct);

        return await ChangedAsync(issue.Id, ct, new { place, of });
    }

    /// <summary>The issue as it now is, with the version the next replacing edit needs.</summary>
    private async Task<string> ChangedAsync(Guid id, CancellationToken ct, object? column = null)
    {
        var zone = await reader.TimeZoneAsync(ct);
        var issue = reader.Db.Issues.AsNoTracking().Where(i => i.Id == id);
        var view = await issue.Select(IssueView.Projection).SingleAsync(ct);
        var updatedAt = await issue.Select(i => i.UpdatedAt).SingleAsync(ct);

        return McpReader.Serialize(new
        {
            issue = view.In(zone),
            version = McpReader.VersionOf(updatedAt),
            column,
            url = reader.WebUrl($"app/issues/{view.Key}")
        });
    }

    private async Task<string> ResultAsync(IssueView issue, bool created, CancellationToken ct, string? note = null)
    {
        var zone = await reader.TimeZoneAsync(ct);
        return McpReader.Serialize(new { created, issue = issue.In(zone), url = reader.WebUrl($"app/issues/{issue.Key}"), note });
    }
}
