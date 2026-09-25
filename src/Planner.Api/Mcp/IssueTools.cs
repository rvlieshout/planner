using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using Planner.Contracts.Common;
using Planner.Contracts.Enums;

namespace Planner.Api.Mcp;

[McpServerToolType]
public sealed class IssueTools(McpReader reader)
{
    [McpServerTool(Name = "search_issues", Title = "Search issues", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Find issues by team, project, state, assignee, text or recent change. Returns summaries, most recently updated first; use get_issue for the description, comments and history of one.")]
    public async Task<string> SearchIssuesAsync(
        [Description("Only this team: its key, name or id.")] string? team = null,
        [Description("Only this project: its name or id.")] string? project = null,
        [Description("Only issues whose state is of these types.")] WorkflowStateType[]? stateTypes = null,
        [Description("Only issues assigned to this person: 'me', an email address or a display name.")] string? assignee = null,
        [Description("Only unassigned issues.")] bool unassigned = false,
        [Description("Only issues with this priority or more urgent.")] IssuePriority? minimumPriority = null,
        [Description("Text to look for in the title and description.")] string? text = null,
        [Description("Only issues changed since this date or moment (ISO 8601).")] string? updatedSince = null,
        [Description("Include archived issues.")] bool includeArchived = false,
        [Description("How many to return, at most 100.")] int limit = 50,
        CancellationToken ct = default)
    {
        var readable = await reader.ReadableTeamIdsAsync(ct);
        var query = reader.Db.Issues.AsNoTracking().Where(i => readable.Contains(i.TeamId));

        Guid? teamId = null;
        if (team is not null)
        {
            teamId = (await reader.ResolveTeamAsync(team, ct)).Id;
            query = query.Where(i => i.TeamId == teamId);
        }

        if (project is not null)
        {
            var projectId = (await reader.ResolveProjectAsync(project, teamId, ct)).Id;
            query = query.Where(i => i.ProjectId == projectId);
        }

        if (stateTypes is { Length: > 0 })
        {
            query = query.Where(i => stateTypes.Contains(i.State.Type));
        }

        if (unassigned)
        {
            query = query.Where(i => i.AssigneeId == null);
        }
        else if (assignee is not null)
        {
            var assigneeId = await reader.ResolveUserAsync(assignee, ct);
            query = query.Where(i => i.AssigneeId == assigneeId);
        }

        // Priorities count down from Urgent (1) to Low (4); None (0) is "not set", not "most urgent".
        if (minimumPriority is { } minimum && minimum != IssuePriority.None)
        {
            query = query.Where(i => i.Priority != IssuePriority.None && i.Priority <= minimum);
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            var search = text.Trim();

            // "DEV-42" means that issue, as it does in the app's search box.
            var separator = search.LastIndexOf('-');
            if (separator > 0 && int.TryParse(search[(separator + 1)..], out var number))
            {
                var key = search[..separator].ToUpperInvariant();
                query = query.Where(i => i.Team.Key == key && i.Number == number);
            }
            else
            {
                var pattern = $"%{search}%";
                query = query.Where(i =>
                    EF.Functions.ILike(i.Title, pattern) ||
                    (i.Description != null && EF.Functions.ILike(i.Description, pattern)));
            }
        }

        if (updatedSince is not null)
        {
            var since = await reader.ParseMomentAsync(updatedSince, ct);
            query = query.Where(i => i.UpdatedAt >= since);
        }

        if (!includeArchived)
        {
            query = query.Where(i => i.ArchivedAt == null);
        }

        var take = Math.Clamp(limit, 1, 100);
        var total = await query.LongCountAsync(ct);
        var issues = await query
            .OrderByDescending(i => i.UpdatedAt)
            .Take(take)
            .Select(IssueView.Projection)
            .ToListAsync(ct);

        var zone = await reader.TimeZoneAsync(ct);
        return McpReader.Serialize(new Page<IssueView>(issues.Select(i => i.In(zone)).ToList(), total, total > take));
    }

    [McpServerTool(Name = "get_issue", Title = "Get an issue", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("One issue in full: description, sub-issues, relations, the latest comments and its change history.")]
    public async Task<string> GetIssueAsync(
        [Description("The issue key, e.g. DEV-42.")] string key,
        CancellationToken ct = default)
    {
        var found = await reader.ResolveIssueAsync(key, ct);
        var db = reader.Db;
        var zone = await reader.TimeZoneAsync(ct);

        var issue = await db.Issues.AsNoTracking().Where(i => i.Id == found.Id).Select(IssueView.Projection).SingleAsync(ct);

        var details = await db.Issues.AsNoTracking()
            .Where(i => i.Id == found.Id)
            .Select(i => new
            {
                i.Description,
                Creator = i.Creator.DisplayName,
                Parent = i.Parent != null ? i.Parent.Team.Key + "-" + i.Parent.Number + " " + i.Parent.Title : null
            })
            .SingleAsync(ct);

        var subIssues = await db.Issues.AsNoTracking()
            .Where(i => i.ParentId == found.Id)
            .OrderBy(i => i.Rank)
            .Select(IssueView.Projection)
            .ToListAsync(ct);

        // Stored once per pair; read from this issue's side, "blocks" seen from the target is "blocked by".
        var outgoing = await db.Set<Domain.Entities.IssueRelation>().AsNoTracking()
            .Where(r => r.SourceIssueId == found.Id)
            .Select(r => new RelationView(
                r.Type == IssueRelationType.Blocks ? "blocks" : r.Type == IssueRelationType.Duplicates ? "duplicates" : "related",
                r.TargetIssue.Team.Key + "-" + r.TargetIssue.Number, r.TargetIssue.Title, r.TargetIssue.State.Name))
            .ToListAsync(ct);

        var incoming = await db.Set<Domain.Entities.IssueRelation>().AsNoTracking()
            .Where(r => r.TargetIssueId == found.Id)
            .Select(r => new RelationView(
                r.Type == IssueRelationType.Blocks ? "blocked_by" : r.Type == IssueRelationType.Duplicates ? "duplicated_by" : "related",
                r.SourceIssue.Team.Key + "-" + r.SourceIssue.Number, r.SourceIssue.Title, r.SourceIssue.State.Name))
            .ToListAsync(ct);

        const int commentLimit = 30;
        var commentTotal = await db.Comments.CountAsync(c => c.IssueId == found.Id, ct);
        var comments = await db.Comments.AsNoTracking()
            .Where(c => c.IssueId == found.Id)
            .OrderByDescending(c => c.CreatedAt)
            .Take(commentLimit)
            .Select(c => new CommentView(c.Author.DisplayName, c.CreatedAt, c.Body, c.ParentCommentId != null))
            .ToListAsync(ct);

        var files = await db.Attachments.AsNoTracking()
            .Where(a => a.IssueId == found.Id)
            .OrderBy(a => a.CreatedAt)
            .Select(a => new { a.Id, a.FileName, a.SizeBytes, UploadedBy = a.UploadedBy.DisplayName, a.CreatedAt, a.StorageUri })
            .ToListAsync(ct);

        var history = await FeedTools.ActivityAsync(reader,
            db.ActivityEvents.Where(a => a.IssueId == found.Id), 50, ct);

        return McpReader.Serialize(new
        {
            issue = issue.In(zone),
            details.Description,
            details.Creator,
            details.Parent,
            subIssues = subIssues.Select(i => i.In(zone)),
            relations = outgoing.Concat(incoming),
            comments = comments
                .OrderBy(c => c.At)
                .Select(c => c with { At = TimeZoneInfo.ConvertTime(c.At, zone) }),
            commentsOmitted = Math.Max(0, commentTotal - commentLimit),
            // Read text files with read_attachment; a link points somewhere outside Planner.
            attachments = files.Select(a => new
            {
                id = a.Id.ToBase58(),
                a.FileName,
                a.SizeBytes,
                a.UploadedBy,
                at = TimeZoneInfo.ConvertTime(a.CreatedAt, zone),
                link = a.StorageUri.StartsWith("planner-attachment:", StringComparison.Ordinal) ? null : a.StorageUri
            }),
            history,
            // update_issue needs this to replace the description, so a newer edit is never overwritten.
            version = McpReader.VersionOf(found.UpdatedAt),
            url = reader.WebUrl($"app/issues/{issue.Key}")
        });
    }
}
