using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Planner.Api.Endpoints;
using Planner.Contracts.Common;
using Planner.Contracts.Enums;
using Planner.Contracts.Projects;

namespace Planner.Api.Mcp;

/// <summary>A project's brief and its documents: where people, and the agents working with them, keep
/// what a project is, where it stands, how it is built and where it is going. Writes go through the same
/// commands as the REST API. Replacing text needs the version it was read at; appending never does.</summary>
[McpServerToolType]
public sealed class ProjectDocumentTools(McpReader reader, ProjectCommands projects, DocumentCommands documents)
{
    private static readonly string[] ProjectClearable = ["summary", "description", "lead", "startDate", "targetDate"];

    [McpServerTool(Name = "update_project", Title = "Edit a project", ReadOnly = false, Destructive = false,
        Idempotent = true, OpenWorld = false)]
    [Description(
        "Change a project's brief and settings, as the user: its description (the brief: what the project is, " +
        "where it stands, where it is going), one-line summary, status, health, lead and dates. Only what is " +
        "passed changes. Prefer appendToDescription for adding to the brief; replacing it with `description` " +
        "needs the `version` from get_project and is refused if the project changed since. Longer material " +
        "(architecture, decisions, plans) belongs in documents; see create_document.")]
    public async Task<string> UpdateProjectAsync(
        [Description("The project: its name (or a unique part of it) or id.")] string project,
        [Description("The team it belongs to, when the name alone is ambiguous.")] string? team = null,
        [Description("A new brief replacing the old one entirely, in Markdown. Needs `version`.")] string? description = null,
        [Description("Markdown added to the end of the brief as a new paragraph. Needs no version.")] string? appendToDescription = null,
        [Description("The project's `version` from get_project. Required to replace or clear the description.")] string? version = null,
        [Description("A new one-line summary shown in lists.")] string? summary = null,
        [Description("A new name.")] string? name = null,
        [Description("A new status.")] ProjectStatus? status = null,
        [Description("A new health.")] ProjectHealth? health = null,
        [Description("A new lead: 'me', an email address or a display name.")] string? lead = null,
        [Description("A new start date, as an ISO date.")] string? startDate = null,
        [Description("A new target date, as an ISO date.")] string? targetDate = null,
        [Description("Fields to empty: summary, description, lead, startDate, targetDate.")] string[]? clear = null,
        CancellationToken ct = default)
    {
        var teamId = team is null ? (Guid?)null : (await reader.ResolveTeamAsync(team, ct)).Id;
        var found = await reader.ResolveProjectAsync(project, teamId, ct);
        var cleared = Cleared(clear, ProjectClearable);

        if (description is not null && appendToDescription is not null)
        {
            throw new McpException("Pass either `description` (replace) or `appendToDescription` (add), not both.");
        }

        if (description is not null || cleared.Contains("description"))
        {
            McpReader.RequireCurrent(version, found.UpdatedAt, "project's description");
        }

        static Optional<T> Set<T>(bool set, T value) => set ? Optional<T>.From(value) : default;

        var request = new UpdateProjectRequest(
            Name: Set(name is not null, name!),
            Summary: summary is not null ? Optional<string?>.From(summary) : Set<string?>(cleared.Contains("summary"), null),
            Description: description is not null ? Optional<string?>.From(description)
                : appendToDescription is not null ? Optional<string?>.From(McpReader.Append(found.Description, appendToDescription))
                : Set<string?>(cleared.Contains("description"), null),
            Status: Set(status is not null, status.GetValueOrDefault()),
            Health: Set(health is not null, health.GetValueOrDefault()),
            Color: default,
            LeadUserId: lead is not null ? Optional<Guid?>.From(await reader.ResolveUserAsync(lead, ct)) : Set<Guid?>(cleared.Contains("lead"), null),
            StartDate: startDate is not null ? Optional<DateOnly?>.From(McpReader.ParseDate(startDate)) : Set<DateOnly?>(cleared.Contains("startDate"), null),
            TargetDate: targetDate is not null ? Optional<DateOnly?>.From(McpReader.ParseDate(targetDate)) : Set<DateOnly?>(cleared.Contains("targetDate"), null),
            Rank: default);

        if (!request.Name.IsSet && !request.Summary.IsSet && !request.Description.IsSet && !request.Status.IsSet &&
            !request.Health.IsSet && !request.LeadUserId.IsSet && !request.StartDate.IsSet && !request.TargetDate.IsSet)
        {
            throw new McpException("Nothing to change: pass at least one field, or list fields in `clear`.");
        }

        var outcome = await projects.UpdateAsync(found.Id, request, ct);
        if (outcome.Value is null)
        {
            throw new McpException(outcome.ErrorMessage ?? "The project could not be updated.");
        }

        var updatedAt = await reader.Db.Projects.Where(p => p.Id == found.Id).Select(p => p.UpdatedAt).SingleAsync(ct);
        return McpReader.Serialize(new
        {
            project = outcome.Value.Name,
            team = outcome.Value.TeamKey,
            outcome.Value.Status,
            outcome.Value.Health,
            descriptionLength = outcome.Value.Description?.Length ?? 0,
            version = McpReader.VersionOf(updatedAt),
            url = reader.WebUrl($"app/projects/{found.Id.ToBase58()}")
        });
    }

    [McpServerTool(Name = "list_documents", Title = "List documents", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Documents the user can read (specs, architecture notes, decision records, status write-ups), most recently changed first. Read one with get_document.")]
    public async Task<string> ListDocumentsAsync(
        [Description("Only this team: its key, name or id.")] string? team = null,
        [Description("Only this project: its name or id.")] string? project = null,
        [Description("Only documents whose title or content contains this text.")] string? search = null,
        [Description("Include archived documents.")] bool includeArchived = false,
        CancellationToken ct = default)
    {
        var readable = await reader.ReadableTeamIdsAsync(ct);
        var query = reader.Db.Documents.AsNoTracking().Where(d => readable.Contains(d.TeamId));

        Guid? teamId = null;
        if (team is not null)
        {
            teamId = (await reader.ResolveTeamAsync(team, ct)).Id;
            query = query.Where(d => d.TeamId == teamId);
        }

        if (project is not null)
        {
            var projectId = (await reader.ResolveProjectAsync(project, teamId, ct)).Id;
            query = query.Where(d => d.ProjectId == projectId);
        }

        if (!includeArchived)
        {
            query = query.Where(d => d.ArchivedAt == null);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(d => EF.Functions.ILike(d.Title, pattern) || EF.Functions.ILike(d.Content, pattern));
        }

        var zone = await reader.TimeZoneAsync(ct);
        var items = await query
            .OrderByDescending(d => d.UpdatedAt)
            .Take(200)
            .Select(d => new
            {
                d.Id,
                d.Title,
                Team = d.Team.Key,
                Project = d.Project != null ? d.Project.Name : null,
                UpdatedBy = d.UpdatedBy != null ? d.UpdatedBy.DisplayName : d.CreatedBy.DisplayName,
                d.UpdatedAt,
                Length = d.Content.Length,
                Archived = d.ArchivedAt != null
            })
            .ToListAsync(ct);

        return McpReader.Serialize(items.Select(d => new
        {
            id = d.Id.ToBase58(),
            d.Title,
            d.Team,
            d.Project,
            d.UpdatedBy,
            updatedAt = TimeZoneInfo.ConvertTime(d.UpdatedAt, zone),
            d.Length,
            archived = d.Archived ? true : (bool?)null
        }));
    }

    [McpServerTool(Name = "get_document", Title = "Read a document", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("A document's full Markdown content, with the `version` needed to replace it with update_document.")]
    public async Task<string> GetDocumentAsync(
        [Description("The document: its id, or its title (or a unique part of it).")] string document,
        [Description("Its team, when the title alone is ambiguous.")] string? team = null,
        [Description("Its project, when the title alone is ambiguous.")] string? project = null,
        CancellationToken ct = default)
    {
        var found = await ResolveAsync(document, team, project, ct);
        var zone = await reader.TimeZoneAsync(ct);

        var people = await reader.Db.Documents.AsNoTracking().Where(d => d.Id == found.Id)
            .Select(d => new { CreatedBy = d.CreatedBy.DisplayName, UpdatedBy = d.UpdatedBy != null ? d.UpdatedBy.DisplayName : null })
            .SingleAsync(ct);

        return McpReader.Serialize(new
        {
            id = found.Id.ToBase58(),
            found.Title,
            team = found.Team.Key,
            project = found.Project?.Name,
            found.Content,
            people.CreatedBy,
            people.UpdatedBy,
            createdAt = TimeZoneInfo.ConvertTime(found.CreatedAt, zone),
            updatedAt = TimeZoneInfo.ConvertTime(found.UpdatedAt, zone),
            archived = found.ArchivedAt is not null ? true : (bool?)null,
            version = McpReader.VersionOf(found.UpdatedAt),
            url = found.ProjectId is { } p ? reader.WebUrl($"app/projects/{p.ToBase58()}") : null
        });
    }

    [McpServerTool(Name = "create_document", Title = "Create a document", ReadOnly = false, Destructive = false,
        Idempotent = false, OpenWorld = false)]
    [Description(
        "Create a Markdown document in a team, optionally on a project, as the user. For the state of a " +
        "project (overview, current status, architecture, direction, decisions) keep one document per " +
        "subject and update it rather than creating another: a document with the same title in the same " +
        "place is refused, and update_document is suggested instead.")]
    public async Task<string> CreateDocumentAsync(
        [Description("The team: its key, name or id.")] string team,
        [Description("The title, up to 300 characters.")] string title,
        [Description("The content, in Markdown.")] string content,
        [Description("The project it belongs to, by name or id.")] string? project = null,
        CancellationToken ct = default)
    {
        var teamId = (await reader.ResolveTeamAsync(team, ct)).Id;
        Guid? projectId = project is null ? null : (await reader.ResolveProjectAsync(project, teamId, ct)).Id;

        var normalized = title.Trim().ToUpper();
        var existing = await reader.Db.Documents.AsNoTracking()
            .Where(d => d.TeamId == teamId && d.ProjectId == projectId && d.ArchivedAt == null && d.Title.ToUpper() == normalized)
            .Select(d => (Guid?)d.Id)
            .FirstOrDefaultAsync(ct);

        if (existing is { } id)
        {
            throw new McpException(
                $"A document titled '{title.Trim()}' already exists here (id {id.ToBase58()}). " +
                "Read it with get_document and change it with update_document instead of creating another.");
        }

        var outcome = await documents.CreateAsync(new CreateDocumentRequest(teamId, projectId, title, content), ct);
        if (outcome.Value is null)
        {
            throw new McpException(outcome.ErrorMessage ?? "The document could not be created.");
        }

        return await ChangedAsync(outcome.Value.Id, created: true, ct);
    }

    [McpServerTool(Name = "update_document", Title = "Edit a document", ReadOnly = false, Destructive = false,
        Idempotent = true, OpenWorld = false)]
    [Description(
        "Change a document, as the user: rename it, add to it with `append`, or replace its content with " +
        "`content`. Replacing needs the `version` from get_document and is refused if the document changed " +
        "since, so read, edit the text you read, and write it back. Appending never needs a version.")]
    public async Task<string> UpdateDocumentAsync(
        [Description("The document: its id, or its title (or a unique part of it).")] string document,
        [Description("The new full content, in Markdown, replacing the old. Needs `version`.")] string? content = null,
        [Description("Markdown added to the end as a new paragraph. Needs no version.")] string? append = null,
        [Description("The document's `version` from get_document. Required with `content`.")] string? version = null,
        [Description("A new title.")] string? title = null,
        [Description("Its team, when the title alone is ambiguous.")] string? team = null,
        [Description("Its project, when the title alone is ambiguous.")] string? project = null,
        CancellationToken ct = default)
    {
        var found = await ResolveAsync(document, team, project, ct);

        if (content is not null && append is not null)
        {
            throw new McpException("Pass either `content` (replace) or `append` (add), not both.");
        }

        if (content is null && append is null && title is null)
        {
            throw new McpException("Nothing to change: pass content, append or title.");
        }

        if (content is not null)
        {
            McpReader.RequireCurrent(version, found.UpdatedAt, "document");
        }

        var newContent = content ?? (append is not null ? McpReader.Append(found.Content, append) : null);

        var outcome = await documents.UpdateAsync(found.Id, new UpdateDocumentRequest(
            Title: title is null ? default : Optional<string>.From(title),
            Content: newContent is null ? default : Optional<string>.From(newContent),
            ProjectId: default), ct);

        if (outcome.Value is null)
        {
            throw new McpException(outcome.ErrorMessage ?? "The document could not be updated.");
        }

        return await ChangedAsync(found.Id, created: false, ct);
    }

    private async Task<Domain.Entities.Document> ResolveAsync(string document, string? team, string? project, CancellationToken ct)
    {
        var teamId = team is null ? (Guid?)null : (await reader.ResolveTeamAsync(team, ct)).Id;
        var projectId = project is null ? (Guid?)null : (await reader.ResolveProjectAsync(project, teamId, ct)).Id;
        return await reader.ResolveDocumentAsync(document, teamId, projectId, ct);
    }

    private async Task<string> ChangedAsync(Guid id, bool created, CancellationToken ct)
    {
        var now = await reader.Db.Documents.AsNoTracking().Where(d => d.Id == id)
            .Select(d => new { d.Title, d.ProjectId, Team = d.Team.Key, Project = d.Project != null ? d.Project.Name : null, d.Content.Length, d.UpdatedAt })
            .SingleAsync(ct);

        return McpReader.Serialize(new
        {
            created,
            id = id.ToBase58(),
            now.Title,
            now.Team,
            now.Project,
            length = now.Length,
            version = McpReader.VersionOf(now.UpdatedAt),
            url = now.ProjectId is { } p ? reader.WebUrl($"app/projects/{p.ToBase58()}") : null
        });
    }

    private static HashSet<string> Cleared(string[]? clear, string[] allowed)
    {
        var cleared = new HashSet<string>(clear ?? [], StringComparer.OrdinalIgnoreCase);

        return cleared.FirstOrDefault(c => !allowed.Contains(c, StringComparer.OrdinalIgnoreCase)) is { } unknown
            ? throw new McpException($"'{unknown}' cannot be cleared. Clearable fields: {string.Join(", ", allowed)}.")
            : cleared;
    }
}
