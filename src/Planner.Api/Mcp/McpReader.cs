using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using Planner.Api.Auth;
using Planner.Api.Authorization;
using Planner.Contracts.Common;
using Planner.Contracts.Enums;
using Planner.Domain.Entities;
using Planner.Infrastructure;

namespace Planner.Api.Mcp;

/// <summary>What every tool starts from: the teams the caller can read, names resolved the way a person
/// types them, and times in the caller's time zone.
///
/// Every query a tool runs is scoped through <see cref="ReadableTeamIdsAsync"/>, the same rule the REST
/// endpoints apply, so an assistant sees exactly what its user sees in the app.</summary>
public sealed class McpReader(
    PlannerDbContext db,
    ITeamAccess access,
    CurrentUser user,
    IHttpContextAccessor http,
    IOptions<PlannerAuthOptions> auth)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private IReadOnlyList<Guid>? _readable;
    private TimeZoneInfo? _timeZone;

    public PlannerDbContext Db => db;

    public Guid UserId => user.Id;

    public async Task<IReadOnlyList<Guid>> ReadableTeamIdsAsync(CancellationToken ct) =>
        _readable ??= await access.ReadableTeamIdsAsync(ct);

    /// <summary>Tool results are compact JSON: enums by name, nothing null. Models read it well and it
    /// costs a fraction of the tokens of the REST DTOs, which carry colours and ranks for the UI.</summary>
    public static string Serialize(object value) => JsonSerializer.Serialize(value, Json);

    public async Task<TimeZoneInfo> TimeZoneAsync(CancellationToken ct)
    {
        if (_timeZone is not null)
        {
            return _timeZone;
        }

        var id = await db.Users.Where(u => u.Id == user.Id).Select(u => u.TimeZone).FirstOrDefaultAsync(ct);

        // An id this host cannot resolve should not make every answer fail; UTC is honest about it.
        return _timeZone = id is not null && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone)
            ? zone
            : TimeZoneInfo.Utc;
    }

    public async Task<DateTimeOffset> LocalAsync(DateTimeOffset value, CancellationToken ct) =>
        TimeZoneInfo.ConvertTime(value, await TimeZoneAsync(ct));

    public async Task<DateTimeOffset?> LocalAsync(DateTimeOffset? value, CancellationToken ct) =>
        value is { } v ? await LocalAsync(v, ct) : null;

    /// <summary>Accepts a team key ("DEV"), a name ("Development", any case), a unique part of a name,
    /// or an id. An unknown team is answered with the list of known ones, so the model can correct
    /// itself instead of guessing again.</summary>
    public async Task<Team> ResolveTeamAsync(string team, CancellationToken ct)
    {
        var readable = await ReadableTeamIdsAsync(ct);
        var teams = await db.Teams.AsNoTracking().Where(t => readable.Contains(t.Id)).ToListAsync(ct);
        var wanted = team.Trim();

        var match =
            (Base58.TryParseId(wanted, out var id) ? teams.FirstOrDefault(t => t.Id == id) : null)
            ?? teams.FirstOrDefault(t => string.Equals(t.Key, wanted, StringComparison.OrdinalIgnoreCase))
            ?? teams.FirstOrDefault(t => string.Equals(t.Name, wanted, StringComparison.OrdinalIgnoreCase))
            ?? Unique(teams.Where(t => t.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase)));

        return match ?? throw new McpException(
            $"No team matches '{wanted}'. Teams you can see: " +
            (teams.Count == 0 ? "none." : string.Join(", ", teams.OrderBy(t => t.Key).Select(t => $"{t.Key} ({t.Name})"))));
    }

    /// <summary>Accepts a project name (any case), a unique part of one, or an id; optionally within
    /// one team.</summary>
    public async Task<Project> ResolveProjectAsync(string project, Guid? teamId, CancellationToken ct)
    {
        var readable = await ReadableTeamIdsAsync(ct);
        var query = db.Projects.AsNoTracking().Include(p => p.Team).Where(p => readable.Contains(p.TeamId));

        if (teamId is { } scoped)
        {
            query = query.Where(p => p.TeamId == scoped);
        }

        var projects = await query.ToListAsync(ct);
        var wanted = project.Trim();

        if (Base58.TryParseId(wanted, out var id) && projects.FirstOrDefault(p => p.Id == id) is { } byId)
        {
            return byId;
        }

        // Live projects first: an archived namesake should not shadow the one people are working on.
        var live = projects.Where(p => p.ArchivedAt is null).ToList();
        var named = live.Where(p => string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase)).ToList();

        if (named.Count == 0)
        {
            named = live.Where(p => p.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (named.Count == 0)
        {
            named = projects.Where(p => string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // Two teams can each have a project of the same name. Picking one would answer confidently
        // about the wrong project, so the model is asked to say which.
        return named.Count switch
        {
            1 => named[0],
            0 => throw new McpException(
                $"No project matches '{wanted}'. Use list_projects to see the projects you can read."),
            _ => throw new McpException(
                $"'{wanted}' matches more than one project: " +
                string.Join(", ", named.Select(p => $"{p.Name} ({p.Team.Key})")) +
                ". Pass the team as well, or the project id from list_projects.")
        };
    }

    /// <summary>"me", an email address, or a display name.</summary>
    public async Task<Guid> ResolveUserAsync(string who, CancellationToken ct)
    {
        var wanted = who.Trim();

        if (string.Equals(wanted, "me", StringComparison.OrdinalIgnoreCase))
        {
            return user.Id;
        }

        var normalized = wanted.ToUpperInvariant();
        var matches = await db.Users.AsNoTracking()
            .Where(u => u.NormalizedEmail == normalized || u.DisplayName.ToUpper() == normalized)
            .Select(u => u.Id)
            .Take(2)
            .ToListAsync(ct);

        return matches.Count == 1
            ? matches[0]
            : throw new McpException(matches.Count == 0
                ? $"No user matches '{wanted}'. Use an email address, a display name, or 'me'."
                : $"More than one user is called '{wanted}'. Use their email address instead.");
    }

    public async Task<Issue> ResolveIssueAsync(string key, CancellationToken ct)
    {
        var wanted = key.Trim();
        var separator = wanted.LastIndexOf('-');

        if (separator <= 0 || !int.TryParse(wanted[(separator + 1)..], out var number))
        {
            throw new McpException($"'{wanted}' is not an issue key. Keys look like DEV-42.");
        }

        var teamKey = wanted[..separator].ToUpperInvariant();
        var readable = await ReadableTeamIdsAsync(ct);

        return await db.Issues.AsNoTracking()
                   .FirstOrDefaultAsync(i => i.Team.Key == teamKey && i.Number == number && readable.Contains(i.TeamId), ct)
               ?? throw new McpException($"Issue {wanted.ToUpperInvariant()} does not exist or you cannot see it.");
    }

    /// <summary>The start of a reporting window: an explicit date wins, otherwise a number of days back
    /// from now. A bare date means midnight in the user's time zone, which is what "since Monday" means
    /// to the person asking.</summary>
    public async Task<DateTimeOffset> WindowStartAsync(string? since, int? days, int defaultDays, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(since))
        {
            return await ParseMomentAsync(since, ct);
        }

        var span = Math.Clamp(days ?? defaultDays, 1, 366);
        return DateTimeOffset.UtcNow.AddDays(-span);
    }

    public async Task<DateTimeOffset> ParseMomentAsync(string text, CancellationToken ct)
    {
        var value = text.Trim();

        if (DateOnly.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var date))
        {
            var zone = await TimeZoneAsync(ct);
            var local = date.ToDateTime(TimeOnly.MinValue);
            return new DateTimeOffset(local, zone.GetUtcOffset(local));
        }

        if (DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var moment))
        {
            return moment;
        }

        throw new McpException($"'{value}' is not a date. Use ISO 8601, e.g. 2026-09-18 or 2026-09-18T09:00:00Z.");
    }

    /// <summary>A link the user can open. From the configured public address when there is one, so it is
    /// right behind a proxy, and from the request otherwise.</summary>
    public string? WebUrl(string path)
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
    public async Task<Guid> ResolveStateAsync(Guid teamId, string state, CancellationToken ct)
    {
        var states = await db.WorkflowStates.AsNoTracking()
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

    public async Task<Guid> ResolveMilestoneAsync(Guid projectId, string milestone, CancellationToken ct)
    {
        var milestones = await db.Milestones.AsNoTracking()
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
    public async Task<IReadOnlyList<Guid>> ResolveLabelsAsync(Guid teamId, string[] names, CancellationToken ct)
    {
        var usable = await db.Labels.AsNoTracking()
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

    public static DateOnly ParseDate(string value) =>
        DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new McpException($"'{value}' is not a date. Use an ISO date such as 2026-10-15.");

    /// <summary>An opaque token for "the version I read", from the row's last-modified time. Text an agent
    /// replaces wholesale (a description, a document) is checked against it, so an edit a person made in
    /// between is never silently overwritten.</summary>
    public static string VersionOf(DateTimeOffset updatedAt) => updatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture);

    /// <summary>Refuses a replacement made from a stale read, or made blind.</summary>
    public static void RequireCurrent(string? version, DateTimeOffset updatedAt, string what)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new McpException(
                $"Replacing the {what} needs the `version` from when you read it, so nobody's edits are lost. " +
                "Read it first, or append instead of replacing.");
        }

        if (version.Trim() != VersionOf(updatedAt))
        {
            throw new McpException(
                $"The {what} has changed since you read it. Read it again and reapply your edit to the new text.");
        }
    }

    /// <summary>A document by id, or by title (any case, then a unique part of one) among those the user
    /// can read, optionally within one team or project.</summary>
    public async Task<Document> ResolveDocumentAsync(string document, Guid? teamId, Guid? projectId, CancellationToken ct)
    {
        var readable = await ReadableTeamIdsAsync(ct);
        var query = db.Documents.AsNoTracking().Include(d => d.Team).Include(d => d.Project)
            .Where(d => readable.Contains(d.TeamId));

        if (teamId is { } team)
        {
            query = query.Where(d => d.TeamId == team);
        }

        if (projectId is { } project)
        {
            query = query.Where(d => d.ProjectId == project);
        }

        var wanted = document.Trim();

        if (Base58.TryParseId(wanted, out var id) && await query.FirstOrDefaultAsync(d => d.Id == id, ct) is { } byId)
        {
            return byId;
        }

        var candidates = await query.Where(d => d.ArchivedAt == null).ToListAsync(ct);
        var named = candidates.Where(d => string.Equals(d.Title, wanted, StringComparison.OrdinalIgnoreCase)).ToList();

        if (named.Count == 0)
        {
            named = candidates.Where(d => d.Title.Contains(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return named.Count switch
        {
            1 => named[0],
            0 => throw new McpException($"No document matches '{wanted}'. Use list_documents to see the documents you can read."),
            _ => throw new McpException(
                $"'{wanted}' matches more than one document: " +
                string.Join(", ", named.Select(d => $"\"{d.Title}\" ({d.Team.Key}{(d.Project is null ? "" : ", " + d.Project.Name)}, id {d.Id.ToBase58()})")) +
                ". Pass the id, or the team or project as well.")
        };
    }

    /// <summary>Joins an addition onto existing Markdown as its own paragraph.</summary>
    public static string Append(string? existing, string addition) =>
        string.IsNullOrWhiteSpace(existing) ? addition.Trim() : existing.TrimEnd() + "\n\n" + addition.Trim();

    private static T? Unique<T>(IEnumerable<T> candidates) where T : class
    {
        using var e = candidates.GetEnumerator();
        if (!e.MoveNext())
        {
            return null;
        }

        var first = e.Current;
        return e.MoveNext() ? null : first;
    }
}
