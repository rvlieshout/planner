using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using Planner.Api.Notifications;
using Planner.Domain.Entities;

namespace Planner.Api.Mcp;

[McpServerToolType]
public sealed class FeedTools(McpReader reader)
{
    [McpServerTool(Name = "get_inbox", Title = "Read the inbox", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The user's inbox: changes and comments on issues they follow, newest first. Reading it does not mark anything as read.")]
    public async Task<string> GetInboxAsync(
        [Description("Only entries the user has not read yet.")] bool unreadOnly = true,
        [Description("How many to return, at most 100.")] int limit = 30,
        CancellationToken ct = default)
    {
        var db = reader.Db;
        var inbox = Inbox.For(db, reader.UserId).AsNoTracking();
        var unread = await inbox.CountAsync(n => n.ReadAt == null, ct);

        if (unreadOnly)
        {
            inbox = inbox.Where(n => n.ReadAt == null);
        }

        var take = Math.Clamp(limit, 1, 100);
        var total = await inbox.LongCountAsync(ct);
        var entries = await inbox
            .OrderByDescending(n => n.CreatedAt)
            .ThenByDescending(n => n.Id)
            .Take(take)
            .Select(n => new { n.ActivityEventId, n.ReadAt })
            .ToListAsync(ct);

        var eventIds = entries.Select(e => e.ActivityEventId).ToList();
        var events = (await ActivityWithIdsAsync(reader, db.ActivityEvents.Where(a => eventIds.Contains(a.Id)), take, ct))
            .ToDictionary(e => e.Id, e => e.View);

        return McpReader.Serialize(new
        {
            unread,
            total,
            more = total > take,
            entries = entries
                .Where(e => events.ContainsKey(e.ActivityEventId))
                .Select(e => new { read = e.ReadAt is not null, @event = events[e.ActivityEventId] })
        });
    }

    [McpServerTool(Name = "get_activity", Title = "Read the activity feed", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Raw change history, newest first: who created, moved, assigned, commented on or edited what. For a summary of a period use team_digest instead; this is for detail it leaves out.")]
    public async Task<string> GetActivityAsync(
        [Description("Only this team: its key, name or id.")] string? team = null,
        [Description("Only this project: its name or id.")] string? project = null,
        [Description("From this date or moment (ISO 8601). Defaults to `days` ago.")] string? since = null,
        [Description("How many days back, when `since` is not given.")] int days = 7,
        [Description("Only events by this person: 'me', an email address or a display name.")] string? actor = null,
        [Description("How many to return, at most 200.")] int limit = 100,
        CancellationToken ct = default)
    {
        var readable = await reader.ReadableTeamIdsAsync(ct);
        var from = await reader.WindowStartAsync(since, days, 7, ct);
        var query = reader.Db.ActivityEvents.Where(a =>
            a.TeamId != null && readable.Contains(a.TeamId.Value) && a.CreatedAt >= from);

        Guid? teamId = null;
        if (team is not null)
        {
            teamId = (await reader.ResolveTeamAsync(team, ct)).Id;
            query = query.Where(a => a.TeamId == teamId);
        }

        if (project is not null)
        {
            var projectId = (await reader.ResolveProjectAsync(project, teamId, ct)).Id;
            query = query.Where(a => a.ProjectId == projectId);
        }

        if (actor is not null)
        {
            var actorId = await reader.ResolveUserAsync(actor, ct);
            query = query.Where(a => a.ActorId == actorId);
        }

        var take = Math.Clamp(limit, 1, 200);
        var total = await query.LongCountAsync(ct);
        var events = await ActivityAsync(reader, query, take, ct);

        return McpReader.Serialize(new
        {
            from = await reader.LocalAsync(from, ct),
            total,
            more = total > take,
            events
        });
    }

    /// <summary>The newest <paramref name="limit"/> events of a query, with actor, team, project and
    /// issue named. Issues and projects are looked up for the page in one query each: the audit trail
    /// has no foreign keys to them, and an event can outlive what it describes.</summary>
    internal static async Task<List<ActivityView>> ActivityAsync(
        McpReader reader, IQueryable<ActivityEvent> query, int limit, CancellationToken ct) =>
        [.. (await ActivityWithIdsAsync(reader, query, limit, ct)).Select(e => e.View)];

    private static async Task<List<(Guid Id, ActivityView View)>> ActivityWithIdsAsync(
        McpReader reader, IQueryable<ActivityEvent> query, int limit, CancellationToken ct)
    {
        var db = reader.Db;
        var zone = await reader.TimeZoneAsync(ct);

        var events = await query.AsNoTracking()
            .OrderByDescending(a => a.CreatedAt)
            .Take(limit)
            .Select(a => new
            {
                a.Id, a.CreatedAt, Actor = a.Actor.DisplayName, a.Action, a.EntityType,
                a.TeamId, a.ProjectId, a.IssueId, a.Data
            })
            .ToListAsync(ct);

        var issueIds = events.Where(e => e.IssueId != null).Select(e => e.IssueId!.Value).Distinct().ToList();
        var projectIds = events.Where(e => e.ProjectId != null).Select(e => e.ProjectId!.Value).Distinct().ToList();
        var teamIds = events.Where(e => e.TeamId != null).Select(e => e.TeamId!.Value).Distinct().ToList();

        var issues = await db.Issues.AsNoTracking()
            .Where(i => issueIds.Contains(i.Id))
            .Select(i => new { i.Id, Key = i.Team.Key + "-" + i.Number, i.Title })
            .ToDictionaryAsync(i => i.Id, ct);
        var projects = await db.Projects.AsNoTracking()
            .Where(p => projectIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var teams = await db.Teams.AsNoTracking()
            .Where(t => teamIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Key, ct);

        return events.Select(e =>
        {
            var issue = e.IssueId is { } issueId ? issues.GetValueOrDefault(issueId) : null;
            return (e.Id, new ActivityView(
                TimeZoneInfo.ConvertTime(e.CreatedAt, zone),
                e.Actor,
                e.Action,
                e.EntityType,
                e.TeamId is { } t ? teams.GetValueOrDefault(t) : null,
                e.ProjectId is { } p ? projects.GetValueOrDefault(p) : null,
                issue?.Key,
                issue?.Title,
                e.Data?.RootElement.Clone()));
        }).ToList();
    }
}
