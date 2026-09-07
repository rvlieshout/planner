using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Client.Services;
using Planner.Client.ViewModels;
using Planner.Contracts.Auth;
using Planner.Contracts.Common;
using Planner.Contracts.Enums;
using Planner.Contracts.Teams;

var handler = new AdministrationServer();
using var http = new HttpClient(handler);
var api = new PlannerApiClient(http, NullLogger<PlannerApiClient>.Instance);
api.UseServer("http://planner.test");
var caller = new MeResponse(Guid.NewGuid(), "admin@test.local", "Admin", null, "UTC", "admin", true, []);
var vm = new UsersViewModel(api, caller) { ConfirmDiscard = _ => Task.FromResult(false) };

await vm.LoadAsync(CancellationToken.None);
Check(vm.Error is null && vm.Users.Count == 2, "Directory follows all pages and includes inactive users");
vm.Search = "inactive";
Check(vm.Users.Count == 1 && !vm.Users[0].IsActive, "Search finds inactive accounts");
vm.Search = "";
Check(!vm.Roles.Contains("owner"), "Admin cannot grant ownership");
await vm.SelectUserCommand.ExecuteAsync(vm.Users[0]);
Check(vm.Error is null && !vm.CanChangeRole && !vm.CanChangeActive, "An owner account cannot be demoted by an admin or deactivated");

await vm.NewUserCommand.ExecuteAsync(null);
vm.DisplayName = "New user";
vm.Email = "new@test.local";
vm.Password = vm.PasswordConfirmation = "long-password-123";
vm.Teams[0].Selection = "Member";
await vm.SelectUserCommand.ExecuteAsync(vm.Users[0]);
Check(vm.IsNew && vm.DisplayName == "New user", "Declining discard preserves the draft");
await vm.SaveCommand.ExecuteAsync(null);
Check(handler.Creates == 1 && vm.IsExisting && vm.Error?.Contains("Account saved") == true,
    "A failed membership retains the created account");
Check(vm.Password == "" && vm.Teams[0].IsDirty, "Creation clears secrets but preserves failed team edits");
await vm.SaveCommand.ExecuteAsync(null);
Check(handler.Creates == 1 && handler.MemberAdds == 2 && vm.Error is null && !vm.HasUnsavedChanges,
    "Retry only finishes outstanding membership changes");
Check(handler.LastPatch == "{}", "Unchanged profile fields are omitted on retry");

vm.Teams[0].Selection = "Viewer";
await vm.SaveCommand.ExecuteAsync(null);
Check(handler.MemberUpdates == 1 && !vm.Teams[0].IsDirty, "Existing membership uses PATCH");
vm.Teams[0].Selection = "Not a member";
await vm.SaveCommand.ExecuteAsync(null);
Check(handler.MemberRemovals == 1 && !vm.Teams[0].IsDirty, "Membership removal handles a 204 response");

vm.Password = "another-password-123";
vm.PasswordConfirmation = "mismatch";
await vm.ResetPasswordCommand.ExecuteAsync(null);
Check(handler.PasswordResets == 0 && vm.Error is not null, "Password mismatch prevents a request");
vm.PasswordConfirmation = vm.Password;
await vm.ResetPasswordCommand.ExecuteAsync(null);
Check(handler.PasswordResets == 1 && vm.Password == "" && vm.Error is null, "Password reset clears secrets after success");

var row = vm.Teams[0];
foreach (var role in new[] { "owner", "admin", "member", "guest" })
foreach (var selection in row.Options)
{
    row.OrgRole = role;
    row.Selection = selection;
    var expected = role is "owner" or "admin" ? "Administer" : selection == "Not a member" ? "No access" :
        role == "guest" ? "Read & comment" : selection == "Lead" ? "Administer" :
        selection == "Member" ? "Read, comment & edit" : "Read only";
    Check(row.EffectiveAccess == expected, $"Effective access: {role} / {selection}");
}

// Teams and their membership, from both sides of the permission rule: an organisation administrator,
// who administers every team and is the only one who can create one, and a team lead, who administers
// the team they lead and nothing else.
var teamHandler = new TeamServer();
using var teamHttp = new HttpClient(teamHandler);
var teamApi = new PlannerApiClient(teamHttp, NullLogger<PlannerApiClient>.Instance);
teamApi.UseServer("http://planner.test");

var lead = new MeResponse(teamHandler.LeadId, "lead@test.local", "Lead", null, "UTC", "member", true,
    [new MeTeamMembership(teamHandler.EngId, "ENG", "Engineering", "Lead")]);
var guest = lead with { Id = Guid.NewGuid(), Role = "guest" };
var admin = new MeResponse(teamHandler.AdminId, "admin@test.local", "Admin", null, "UTC", "admin", true, []);

var leadTeams = new TeamsViewModel(teamApi, NullLogger<TeamsViewModel>.Instance, lead);
await leadTeams.LoadAsync(CancellationToken.None);
Check(leadTeams.Error is null && leadTeams.Teams.Count == 1 && leadTeams.Teams[0].Key == "ENG",
    "A lead administers the team they lead and no other");
Check(!leadTeams.CanCreateTeams && !leadTeams.NewTeamCommand.CanExecute(null),
    "Only an organisation administrator can create a team");

var guestTeams = new TeamsViewModel(teamApi, NullLogger<TeamsViewModel>.Instance, guest);
await guestTeams.LoadAsync(CancellationToken.None);
Check(guestTeams.Teams.Count == 0, "A guest administers nothing, whatever their team role says");

var teams = new TeamsViewModel(teamApi, NullLogger<TeamsViewModel>.Instance, admin)
{
    ConfirmDiscard = _ => Task.FromResult(false)
};
await teams.LoadAsync(CancellationToken.None);
Check(teams.Teams.Count == 3 && teams.Teams[^1].Key == "OLD", "An administrator sees every team, archived ones last");
Check(teams.Teams[^1].Detail.Contains("Archived"), "An archived team says so in the list");

await teams.NewTeamCommand.ExecuteAsync(null);
teams.TeamKey = "web platform";
teams.TeamName = "Web";
await teams.SaveCommand.ExecuteAsync(null);
Check(teamHandler.Creates == 0 && teams.Error is not null && teams.IsNew, "A key the server would refuse is caught before the request");

teams.TeamKey = "web";
await teams.SelectTeamCommand.ExecuteAsync(teams.Teams[0]);
Check(teams.IsNew && teams.TeamName == "Web", "Declining discard preserves the draft team");

await teams.SaveCommand.ExecuteAsync(null);
Check(teamHandler.Creates == 1 && teamHandler.LastCreatedKey == "WEB", "A key is sent upper-cased");
Check(teams.IsEditing && teams.Members.Count == 1 && teams.Members[0].Role == TeamRole.Lead,
    "Creating flows into filling in, with the creator as the team's first lead");
Check(teams.Teams.Count == 4 && teams.Teams.First(t => t.Key == "WEB").MemberCount == 1,
    "A created team joins the list it was created from, counting the lead it already has");

teams.TeamName = "Web platform";
await teams.SaveCommand.ExecuteAsync(null);
Check(teamHandler.LastPatch is not null && teamHandler.LastPatch.Contains("\"name\"") &&
      !teamHandler.LastPatch.Contains("color"), "Only the fields that changed are sent");
Check(teams.Notice == "Saved." && !teams.HasUnsavedChanges, "A saved page says so and holds nothing back");

Check(teams.Candidates.All(c => c.Id != teamHandler.AdminId), "The picker leaves out people already in the team");
teams.SelectedCandidate = teams.Candidates.First(c => c.Id == teamHandler.MemberId);
Check(teams.HasUnsavedChanges, "Somebody picked but not yet added counts as unsaved work");
await teams.AddMemberCommand.ExecuteAsync(null);
Check(teamHandler.MemberAdds == 1 && teams.Members.Count == 2 && teams.SelectedCandidate is null,
    "Adding a member clears the add row");
Check(teams.Teams.First(t => t.Key == "WEB").MemberCount == 2, "The list row counts the members the page holds");

teams.Members[1].SelectedRole = TeamRoleOption.For(TeamRole.Viewer);
Check(teams.Members[1].IsDirty && teams.HasUnsavedChanges, "A changed role is unsaved work on the page, not only on the row");
await teams.SaveCommand.ExecuteAsync(null);
Check(teamHandler.MemberUpdates == 1 && !teams.Members[1].IsDirty, "Save drives every dirty membership row");

await teams.Members[0].RemoveCommand.ExecuteAsync(null);
Check(teams.Members.Count == 2 && teams.Members[0].Error is not null,
    "The last-lead refusal is the server's, and is shown on the row it belongs to");

await teams.ArchiveCommand.ExecuteAsync(null);
Check(teamHandler.Archives == 1 && teams.IsArchived && teams.CanRestore && !teams.CanArchive,
    "Archiving offers restoring in its place");
await teams.RestoreCommand.ExecuteAsync(null);
Check(teamHandler.Restores == 1 && !teams.IsArchived && teams.CanArchive, "Restoring puts the team back");

// A lead who stops being one has just given the page's authority over that team away.
await leadTeams.SelectTeamCommand.ExecuteAsync(leadTeams.Teams[0]);
leadTeams.Members.First(m => m.UserId == teamHandler.LeadId).SelectedRole = TeamRoleOption.For(TeamRole.Member);
await leadTeams.SaveCommand.ExecuteAsync(null);
Check(leadTeams.Teams.Count == 0 && !leadTeams.HasEditor,
    "A lead who demotes themselves loses the team from the page");

Console.WriteLine("All client administration checks passed.");

static void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    Console.WriteLine($"PASS {description}");
}

sealed class AdministrationServer : HttpMessageHandler
{
    private static readonly JsonSerializerOptions Json = OptionalJson.CreateOptions(new JsonStringEnumConverter());
    private readonly Guid _teamId = Guid.NewGuid();
    private readonly UserSummary _created = new(Guid.NewGuid(), "new@test.local", "New user", null, true);
    public int Creates { get; private set; }
    public int MemberAdds { get; private set; }
    public int MemberUpdates { get; private set; }
    public int MemberRemovals { get; private set; }
    public int PasswordResets { get; private set; }
    public string? LastPatch { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Get && path == "/api/v1/users")
        {
            var second = request.RequestUri.Query.Contains("page=2");
            return Ok(new PagedResult<UserSummary>([new(Guid.NewGuid(), second ? "inactive@test.local" : "active@test.local",
                second ? "Inactive" : "Active", null, !second)], second ? 2 : 1, 1, 2));
        }
        if (request.Method == HttpMethod.Get && path == "/api/v1/teams")
            return Ok(new[] { new TeamDto(_teamId, "TEST", "Test team", null, "#ffffff", true, 1,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null) });
        if (request.Method == HttpMethod.Get && path.StartsWith("/api/v1/users/"))
            return Ok(new UserDetail(Guid.Parse(path.Split('/')[4]), "owner@test.local", "Owner", null,
                "UTC", "owner", true, DateTimeOffset.UtcNow, null));
        if (request.Method == HttpMethod.Get && path.EndsWith("/members"))
            return Ok(Array.Empty<TeamMemberDto>());
        if (request.Method == HttpMethod.Post && path == "/api/v1/users")
        { Creates++; return Ok(_created); }
        if (request.Method == HttpMethod.Patch && path == $"/api/v1/users/{_created.Id}")
        {
            LastPatch = await request.Content!.ReadAsStringAsync(ct);
            return Ok(_created);
        }
        if (path == $"/api/v1/teams/{_teamId}/members" && request.Method == HttpMethod.Post)
        {
            MemberAdds++;
            if (MemberAdds == 1)
                return new(HttpStatusCode.Conflict) { Content = JsonContent.Create(new { detail = "Membership conflict." }) };
            return Ok(Member());
        }
        if (path == $"/api/v1/teams/{_teamId}/members/{_created.Id}")
        {
            if (request.Method == HttpMethod.Patch) { MemberUpdates++; return Ok(Member()); }
            if (request.Method == HttpMethod.Delete) { MemberRemovals++; return new(HttpStatusCode.NoContent); }
        }
        if (path == $"/api/v1/users/{_created.Id}/password" && request.Method == HttpMethod.Post)
        { PasswordResets++; return new(HttpStatusCode.NoContent); }
        throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
    }

    private TeamMemberDto Member() => new(_teamId, _created.Id, _created.DisplayName, _created.Email, null, TeamRole.Member, DateTimeOffset.UtcNow);
    private static HttpResponseMessage Ok<T>(T body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body, options: Json) };
}

/// <summary>An installation with three teams and three accounts, enough to exercise the teams page
/// against the two refusals it cannot predict: a duplicate key, and a team's last lead. The last-lead
/// rule is implemented here rather than faked, because the page is supposed to take the server's
/// answer rather than pre-empt it.</summary>
sealed class TeamServer : HttpMessageHandler
{
    private static readonly JsonSerializerOptions Json = OptionalJson.CreateOptions(new JsonStringEnumConverter());

    private readonly Dictionary<Guid, TeamDto> _teams = [];
    private readonly Dictionary<Guid, List<TeamMemberDto>> _members = [];
    private readonly UserSummary[] _directory;

    public Guid EngId { get; } = Guid.NewGuid();
    public Guid OpsId { get; } = Guid.NewGuid();
    public Guid OldId { get; } = Guid.NewGuid();
    public Guid LeadId { get; } = Guid.NewGuid();
    public Guid AdminId { get; } = Guid.NewGuid();
    public Guid MemberId { get; } = Guid.NewGuid();
    public Guid OtherLeadId { get; } = Guid.NewGuid();

    public int Creates { get; private set; }
    public int MemberAdds { get; private set; }
    public int MemberUpdates { get; private set; }
    public int Archives { get; private set; }
    public int Restores { get; private set; }
    public string? LastPatch { get; private set; }
    public string? LastCreatedKey { get; private set; }

    public TeamServer()
    {
        Add(EngId, "ENG", "Engineering", archived: false);
        Add(OpsId, "OPS", "Operations", archived: false);
        Add(OldId, "OLD", "Retired", archived: true);

        _members[EngId] =
        [
            Member(EngId, LeadId, "Lead", TeamRole.Lead),
            Member(EngId, OtherLeadId, "Other lead", TeamRole.Lead)
        ];

        _directory =
        [
            new(AdminId, "admin@test.local", "Admin", null, true),
            new(LeadId, "lead@test.local", "Lead", null, true),
            new(MemberId, "member@test.local", "Member", null, true)
        ];
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (request.Method == HttpMethod.Get && path == "/api/v1/users")
            return Ok(new PagedResult<UserSummary>(_directory, 1, 200, _directory.Length));

        if (request.Method == HttpMethod.Get && path == "/api/v1/teams")
            return Ok(_teams.Values.OrderBy(t => t.Key).ToArray());

        if (request.Method == HttpMethod.Post && path == "/api/v1/teams")
        {
            var body = await Read(request, ct);
            var key = body.GetProperty("key").GetString()!;

            if (_teams.Values.Any(t => t.Key == key))
                return Problem(HttpStatusCode.Conflict, $"A team with key {key} already exists.");

            Creates++;
            LastCreatedKey = key;

            var id = Guid.NewGuid();
            Add(id, key, body.GetProperty("name").GetString()!, archived: false);

            // The creator becomes the first lead, exactly as the API does it.
            _members[id] = [Member(id, AdminId, "Admin", TeamRole.Lead)];
            return Ok(_teams[id] with { MemberCount = 1 });
        }

        if (segments is ["api", "v1", "teams", var rawTeam, ..] && Guid.TryParse(rawTeam, out var teamId))
        {
            if (!_teams.TryGetValue(teamId, out var team))
                return Problem(HttpStatusCode.NotFound, "That team was not found.");

            var members = _members.TryGetValue(teamId, out var list) ? list : _members[teamId] = [];

            if (segments.Length == 4 && request.Method == HttpMethod.Patch)
            {
                LastPatch = await request.Content!.ReadAsStringAsync(ct);
                var body = JsonSerializer.Deserialize<JsonElement>(LastPatch);

                if (body.TryGetProperty("name", out var name))
                    team = team with { Name = name.GetString()! };

                return Ok(_teams[teamId] = team);
            }

            if (segments is [.., "archive"] && request.Method == HttpMethod.Post)
            {
                Archives++;
                return Ok(_teams[teamId] = team with { ArchivedAt = DateTimeOffset.UtcNow });
            }

            if (segments is [.., "restore"] && request.Method == HttpMethod.Post)
            {
                Restores++;
                return Ok(_teams[teamId] = team with { ArchivedAt = null });
            }

            if (segments is [.., "members"])
            {
                if (request.Method == HttpMethod.Get)
                    return Ok(members.OrderByDescending(m => m.Role).ThenBy(m => m.DisplayName).ToArray());

                if (request.Method == HttpMethod.Post)
                {
                    MemberAdds++;
                    var body = await Read(request, ct);
                    var added = body.GetProperty("userId").GetGuid();
                    var role = Enum.Parse<TeamRole>(body.GetProperty("role").GetString()!);
                    var user = _directory.First(u => u.Id == added);
                    var member = Member(teamId, added, user.DisplayName, role);

                    members.Add(member);
                    return Ok(member);
                }
            }

            if (segments is [.., "members", var rawUser] && Guid.TryParse(rawUser, out var userId))
            {
                var member = members.FirstOrDefault(m => m.UserId == userId);
                if (member is null)
                    return Problem(HttpStatusCode.NotFound, "That membership was not found.");

                var lastLead = member.Role == TeamRole.Lead &&
                               !members.Any(m => m.UserId != userId && m.Role == TeamRole.Lead);

                if (request.Method == HttpMethod.Patch)
                {
                    var role = Enum.Parse<TeamRole>((await Read(request, ct)).GetProperty("role").GetString()!);

                    if (lastLead && role != TeamRole.Lead)
                        return Problem(HttpStatusCode.Conflict, "This is the team's only lead. Promote another member first.");

                    MemberUpdates++;
                    var updated = member with { Role = role };
                    members[members.IndexOf(member)] = updated;
                    return Ok(updated);
                }

                if (request.Method == HttpMethod.Delete)
                {
                    if (lastLead)
                        return Problem(HttpStatusCode.Conflict, "This is the team's only lead. Promote another member first.");

                    members.Remove(member);
                    return new(HttpStatusCode.NoContent);
                }
            }
        }

        throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
    }

    private void Add(Guid id, string key, string name, bool archived) =>
        _teams[id] = new TeamDto(id, key, name, null, "#5E6AD2", false, _members.GetValueOrDefault(id)?.Count ?? 0,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, archived ? DateTimeOffset.UtcNow : null);

    private static TeamMemberDto Member(Guid teamId, Guid userId, string name, TeamRole role) =>
        new(teamId, userId, name, $"{name.ToLowerInvariant().Replace(' ', '.')}@test.local", null, role, DateTimeOffset.UtcNow);

    private static async Task<JsonElement> Read(HttpRequestMessage request, CancellationToken ct) =>
        JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(ct));

    private static HttpResponseMessage Ok<T>(T body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body, options: Json) };

    private static HttpResponseMessage Problem(HttpStatusCode status, string detail) =>
        new(status) { Content = JsonContent.Create(new { detail }) };
}
