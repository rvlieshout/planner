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
