using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Planner.Api.Auth;
using Planner.Api.Authorization;
using Planner.Api.Common;
using Planner.Api.Endpoints;
using Planner.Api.Realtime;
using Planner.Contracts.Auth;
using Planner.Contracts.Common;
using Planner.Domain.Identity;
using Planner.Infrastructure;

namespace Planner.Api.Checks;

/// <summary>Real HTTP endpoints, production Identity/EF stores, and independent database contexts
/// for races. Set PLANNER_CHECKS_POSTGRES to a disposable PostgreSQL server connection (CREATEDB
/// permission required). Each run creates and drops only its own uniquely named database.</summary>
public static class InvitationChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var connectionString = Environment.GetEnvironmentVariable("PLANNER_CHECKS_POSTGRES");
        if (string.IsNullOrEmpty(connectionString))
        {
            Console.WriteLine("SKIP: Invitation integration checks require PLANNER_CHECKS_POSTGRES.");
            return;
        }

        var database = "planner_invitations_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(connectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin))
            await create.ExecuteNonQueryAsync();
        var keys = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, database));
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Warning);
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddPlannerPersistence(new NpgsqlConnectionStringBuilder(connectionString)
                { Database = database }.ConnectionString);
            var auth = new PlannerAuthOptions { KeyDirectory = keys.FullName, AllowInsecureHttp = true };
            builder.Services.AddPlannerAuth(auth);
            builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "checks";
                options.DefaultChallengeScheme = "checks";
                options.DefaultForbidScheme = "checks";
            }).AddScheme<AuthenticationSchemeOptions, CheckAuthentication>("checks", _ => { });
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
            builder.Services.AddBase58Ids();
            builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.AddIdConverters());
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddScoped<CurrentUser>();
            builder.Services.AddScoped<Invitations>();
            builder.Services.AddScoped<OpenIddictClientSeeder>();
            builder.Services.AddSignalR();
            builder.Services.AddScoped<RealtimeNotifier>();
            builder.Services.AddScoped<RealtimeSubscriptions>();
            builder.Services.AddSingleton<RealtimeConnections>();
            builder.Services.AddRateLimiter(options => options.AddPolicy("auth", _ => RateLimitPartition.GetNoLimiter("all")));
            await using var app = builder.Build();
            app.UseBase58Ids();
            app.UseAuthentication();
            app.UseRateLimiter();
            app.UseAuthorization();
            app.MapInvitationEndpoints();
            app.MapUserEndpoints();
            app.MapAuthEndpoints();
            using (var scope = app.Services.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<PlannerDbContext>().Database.EnsureCreatedAsync();
                var roles = scope.ServiceProvider.GetRequiredService<RoleManager<AppRole>>();
                foreach (var role in PlannerRoles.All.Keys)
                    await roles.CreateAsync(new AppRole { Name = role });
                await scope.ServiceProvider.GetRequiredService<OpenIddictClientSeeder>().SeedAsync(auth);
            }
            await app.StartAsync();
            try
            {
                await CheckEndpointsAsync(app, auth, check);
            }
            finally
            {
                await app.StopAsync();
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
            keys.Delete(recursive: true);
        }
    }

    private static async Task CheckEndpointsAsync(WebApplication app, PlannerAuthOptions auth, Action<bool, string> check)
    {
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        json.Converters.AddIdConverters();
        void Role(string? role)
        {
            client.DefaultRequestHeaders.Remove("X-Check-Role");
            if (role is not null) client.DefaultRequestHeaders.Add("X-Check-Role", role);
        }
        void As(Guid? userId)
        {
            client.DefaultRequestHeaders.Remove("X-Check-Sub");
            if (userId is { } id) client.DefaultRequestHeaders.Add("X-Check-Sub", id.ToString());
        }
        Task<HttpResponseMessage> Create(string email = "invited@planner.test", string role = "member") =>
            client.PostAsJsonAsync("/api/v1/invitations", new { email, displayName = "Invited User", role });
        Task<HttpResponseMessage> Inspect(InvitationResponse invite, string? token = null, Guid? userId = null) =>
            client.PostAsJsonAsync("/api/v1/invitations/inspect",
                new { userId = userId ?? invite.User.Id, token = token ?? invite.Token }, json);
        Task<HttpResponseMessage> Accept(InvitationResponse invite, string password = "a long chosen password") =>
            client.PostAsJsonAsync("/api/v1/invitations/accept",
                new { userId = invite.User.Id, token = invite.Token, password }, json);
        Task<HttpResponseMessage> Renew(InvitationResponse invite) =>
            client.PostAsync($"/api/v1/invitations/{invite.User.Id.ToBase58()}/renew", null);
        Task<HttpResponseMessage> Login(string email, string password) =>
            client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = auth.WebClientId, ["grant_type"] = "password",
                ["username"] = email, ["password"] = password, ["scope"] = "planner.api"
            }));
        async Task<UserDetail> Detail(InvitationResponse invite) =>
            (await client.GetFromJsonAsync<UserDetail>($"/api/v1/users/{invite.User.Id.ToBase58()}", json))!;
        Task<HttpResponseMessage> Patch(InvitationResponse invite, object body) =>
            client.PatchAsJsonAsync($"/api/v1/users/{invite.User.Id.ToBase58()}", body);
        async Task<InvitationResponse> ReadInvite(HttpResponseMessage response)
        {
            check(response.IsSuccessStatusCode, $"Invitation request succeeds ({response.StatusCode})");
            check(response.Headers.CacheControl?.NoStore == true, "Invitation secrets are marked no-store");
            return (await response.Content.ReadFromJsonAsync<InvitationResponse>(json))!;
        }

        check((await Create()).StatusCode == HttpStatusCode.Unauthorized, "Anonymous callers cannot issue invitations");
        Role("member");
        check((await Create()).StatusCode == HttpStatusCode.Forbidden, "Members cannot issue invitations");
        Role("admin");
        check((await Create(role: "owner")).StatusCode == HttpStatusCode.Forbidden, "Admins cannot invite owners");
        check((await Create(role: "invalid")).StatusCode == HttpStatusCode.BadRequest, "Unknown invitation roles are rejected");
        var invalidEmail = await Create(email: "invalid-email");
        check(invalidEmail.StatusCode == HttpStatusCode.BadRequest,
            $"Identity validates invitation email (received {invalidEmail.StatusCode}: {await invalidEmail.Content.ReadAsStringAsync()})");
        var invite = await ReadInvite(await Create());
        check(invite.User.IsInvitationPending && !invite.User.IsActive && invite.User.Role == "member",
            "New invitation returns full detail for an inactive, pending user with the selected role");
        check(invite.ExpiresAt > DateTimeOffset.UtcNow.AddHours(71) && invite.ExpiresAt <= DateTimeOffset.UtcNow.AddHours(72),
            "Invitation expiry is 72 hours");
        check(invite.User.InvitationExpiresAt == invite.ExpiresAt && (await Detail(invite)).InvitationExpiresAt is not null,
            "The directory records when the pending link lapses");
        check((await Create()).StatusCode == HttpStatusCode.BadRequest, "Duplicate invitation email is rejected");
        check((await client.PatchAsJsonAsync($"/api/v1/users/{invite.User.Id.ToBase58()}", new { isActive = true })).StatusCode == HttpStatusCode.BadRequest,
            "Admin activation cannot bypass invitation acceptance");
        check((await client.PostAsJsonAsync($"/api/v1/users/{invite.User.Id.ToBase58()}/password",
            new { newPassword = "a password by admin" })).StatusCode == HttpStatusCode.BadRequest,
            "Admin password reset cannot bypass invitation acceptance");

        Role(null);
        var pendingLogin = await Login(invite.User.Email, "a long chosen password");
        var unknownLogin = await Login("nobody@planner.test", "a long chosen password");
        check(pendingLogin.StatusCode == HttpStatusCode.BadRequest &&
              await pendingLogin.Content.ReadAsStringAsync() == await unknownLogin.Content.ReadAsStringAsync(),
            "Pending accounts cannot sign in, and are answered exactly as an unknown address is");
        var preview = await Inspect(invite);
        check(preview.IsSuccessStatusCode && preview.Headers.CacheControl?.NoStore == true,
            "Anonymous holder can inspect an invitation without consuming it");
        var details = await preview.Content.ReadFromJsonAsync<InvitationPreview>();
        check(details?.Email == invite.User.Email && details.ExpiresAt == invite.ExpiresAt,
            "Preview identity and expiry match issuance");
        check((await Inspect(invite, token: "malformed")).StatusCode == HttpStatusCode.BadRequest &&
              (await Inspect(invite, token: invite.Token[..^5] + "xxxxx")).StatusCode == HttpStatusCode.BadRequest &&
              (await Inspect(invite, userId: Guid.NewGuid())).StatusCode == HttpStatusCode.BadRequest,
            "Malformed, tampered and wrong-user invitations are rejected");
        var protector = app.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("Planner.Invitations.v1").ToTimeLimitedDataProtector();
        using (var scope = app.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = (await users.FindByIdAsync(invite.User.Id.ToString()))!;
            var expired = WebEncoders.Base64UrlEncode(protector.Protect(
                JsonSerializer.SerializeToUtf8Bytes(new { UserId = user.Id, user.SecurityStamp }),
                DateTimeOffset.UtcNow.AddMinutes(-1)));
            check((await Inspect(invite, token: expired)).StatusCode == HttpStatusCode.BadRequest, "Expired tokens cannot be inspected");
            var reset = await users.GeneratePasswordResetTokenAsync(user);
            check((await Inspect(invite, token: reset)).StatusCode == HttpStatusCode.BadRequest, "Password-reset tokens are not invitations");
        }
        check((await Accept(invite, "short")).StatusCode == HttpStatusCode.BadRequest &&
              (await Inspect(invite)).IsSuccessStatusCode, "Weak passwords do not activate the account or consume its invitation");
        check((await Accept(invite)).StatusCode == HttpStatusCode.NoContent, "Recipient chooses the first password");
        check((await Inspect(invite)).StatusCode == HttpStatusCode.BadRequest &&
              (await Accept(invite)).StatusCode == HttpStatusCode.BadRequest, "Accepted invitations cannot be inspected or replayed");
        check((await Login(invite.User.Email, "a long chosen password")).IsSuccessStatusCode,
            "Accepted account signs in using the chosen password");

        Role("admin");
        check((await Renew(invite)).StatusCode == HttpStatusCode.BadRequest, "Existing accepted users cannot be reinvited");
        var acceptedDetail = await Detail(invite);
        check(!acceptedDetail.IsInvitationPending && acceptedDetail.InvitationExpiresAt is null,
            "Acceptance clears the recorded invitation expiry");
        check((await Patch(invite, new { email = "moved@planner.test" })).StatusCode == HttpStatusCode.BadRequest,
            "An accepted account's email cannot be changed");
        var pending = await ReadInvite(await Create("renwe@planner.test"));
        check((await Patch(pending, new { email = invite.User.Email })).StatusCode == HttpStatusCode.BadRequest,
            "A pending account cannot take another account's email");
        check((await Patch(pending, new { email = "renew@planner.test" })).IsSuccessStatusCode &&
              (await (await Inspect(pending)).Content.ReadFromJsonAsync<InvitationPreview>())?.Email == "renew@planner.test",
            "A mistyped invitation email can be corrected without invalidating the link");
        var renewed = await ReadInvite(await Renew(pending));
        check((await Inspect(pending)).StatusCode == HttpStatusCode.BadRequest && (await Inspect(renewed)).IsSuccessStatusCode,
            "Renewal invalidates old links and supplies a usable replacement");
        check((await client.DeleteAsync($"/api/v1/users/{pending.User.Id.ToBase58()}")).IsSuccessStatusCode &&
              (await Inspect(renewed)).StatusCode == HttpStatusCode.BadRequest, "Deactivation cancels a pending invitation");
        var revokedDetail = await Detail(pending);
        check(revokedDetail.IsInvitationPending && revokedDetail.InvitationExpiresAt is null,
            "A revoked invitation stays pending with no recorded expiry");
        check((await ReadInvite(await Renew(pending))).User.InvitationExpiresAt is not null,
            "Reissuing a revoked invitation records a new expiry");
        Role("owner");
        var owner = await ReadInvite(await Create("owner-invite@planner.test", "owner"));
        Role("admin");
        check((await Renew(owner)).StatusCode == HttpStatusCode.Forbidden, "Admins cannot renew owner invitations");
        check((await client.DeleteAsync($"/api/v1/users/{owner.User.Id.ToBase58()}")).StatusCode == HttpStatusCode.Forbidden,
            "Admins cannot revoke owner invitations");
        Role("owner");
        check((await client.DeleteAsync($"/api/v1/users/{owner.User.Id.ToBase58()}")).IsSuccessStatusCode &&
              (await Inspect(owner)).StatusCode == HttpStatusCode.BadRequest, "Owners can revoke pending owner invitations");
        Role("admin");

        var concurrent = await ReadInvite(await Create("race@planner.test"));
        // Both inspect before either writes: this proves database concurrency protection, not just
        // the HTTP precondition that happens to observe the first request's completed write.
        using var first = app.Services.CreateScope();
        using var second = app.Services.CreateScope();
        var firstInvitations = first.ServiceProvider.GetRequiredService<Invitations>();
        var secondInvitations = second.ServiceProvider.GetRequiredService<Invitations>();
        var one = (await firstInvitations.InspectAsync(concurrent.User.Id, concurrent.Token))!.Value.User;
        var two = (await secondInvitations.InspectAsync(concurrent.User.Id, concurrent.Token))!.Value.User;
        var results = await Task.WhenAll(firstInvitations.AcceptAsync(one, "first chosen password"),
            secondInvitations.AcceptAsync(two, "second chosen password"));
        check(results.Count(result => result.Succeeded) == 1 &&
              results.Single(result => !result.Succeeded).Errors.Any(error => error.Code == "ConcurrencyFailure"),
            "Simultaneous acceptance in separate EF contexts succeeds exactly once");
        Role(null);
        var winningPassword = results[0].Succeeded ? "first chosen password" : "second chosen password";
        check((await Login(concurrent.User.Email, winningPassword)).IsSuccessStatusCode,
            "The single winning password is persisted and can sign in");

        Role("admin");
        var renewalRace = await ReadInvite(await Create("renewal-race@planner.test"));
        using var staleScope = app.Services.CreateScope();
        var staleInvitations = staleScope.ServiceProvider.GetRequiredService<Invitations>();
        var stale = (await staleInvitations.InspectAsync(renewalRace.User.Id, renewalRace.Token))!.Value.User;
        var replacement = await ReadInvite(await Renew(renewalRace));
        var staleResult = await staleInvitations.AcceptAsync(stale, "stale chosen password");
        check(!staleResult.Succeeded && (await Inspect(replacement)).IsSuccessStatusCode,
            "A renewal racing with acceptance prevents stale acceptance and preserves the replacement");
        var legacy = await client.PostAsJsonAsync("/api/v1/users", new
        {
            email = "legacy@planner.test", password = "legacy chosen password", displayName = "Legacy", role = "member"
        });
        check(legacy.StatusCode == HttpStatusCode.Created, "Legacy admin password-based creation remains compatible");

        var legacyId = (await legacy.Content.ReadFromJsonAsync<UserSummary>(json))!.Id.ToBase58();
        check((await client.DeleteAsync($"/api/v1/users/{legacyId}")).IsSuccessStatusCode, "Admins can deactivate an account");
        Role(null);
        var wrongPassword = await Login("legacy@planner.test", "not the chosen password");
        var nobody = await Login("nobody@planner.test", "not the chosen password");
        check(wrongPassword.StatusCode == HttpStatusCode.BadRequest &&
              await wrongPassword.Content.ReadAsStringAsync() == await nobody.Content.ReadAsStringAsync(),
            "A deactivated account is answered as an unknown address is, without its password");
        var rightPassword = await Login("legacy@planner.test", "legacy chosen password");
        check(rightPassword.StatusCode == HttpStatusCode.BadRequest &&
              (await rightPassword.Content.ReadAsStringAsync()).Contains("deactivated"),
            "A deactivated account cannot sign in, and is told why once the password is right");

        Role("owner");
        var secondOwner = await client.PostAsJsonAsync("/api/v1/users", new
        {
            email = "second-owner@planner.test", password = "owner chosen password", displayName = "Second Owner", role = "owner"
        });
        var ownerId = (await secondOwner.Content.ReadFromJsonAsync<UserSummary>(json))!.Id.ToBase58();
        Task<HttpResponseMessage> Reset(string password) =>
            client.PostAsJsonAsync($"/api/v1/users/{ownerId}/password", new { newPassword = password });
        Role("admin");
        check((await Reset("a password by admin")).StatusCode == HttpStatusCode.Forbidden,
            "Admins cannot reset an owner's password");
        Role(null);
        check((await Login("second-owner@planner.test", "owner chosen password")).IsSuccessStatusCode,
            "A refused reset leaves the owner's password as it was");
        Role("owner");
        check((await Reset("a password by owner")).StatusCode == HttpStatusCode.NoContent,
            "Owners can reset an owner's password");

        // The directory. `invite` and `concurrent` are accepted accounts that have signed in and share a
        // team; `renewalRace` is still pending, the legacy account is deactivated, and the second owner
        // is active but in no team.
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlannerDbContext>();
            var team = new Planner.Domain.Entities.Team { Key = "CHK", Name = "Checks" };
            db.Teams.Add(team);
            db.TeamMembers.Add(new Planner.Domain.Entities.TeamMember { TeamId = team.Id, UserId = invite.User.Id });
            db.TeamMembers.Add(new Planner.Domain.Entities.TeamMember { TeamId = team.Id, UserId = concurrent.User.Id });
            await db.SaveChangesAsync();
        }

        async Task<List<UserSummary>> Listed() =>
            [.. (await client.GetFromJsonAsync<PagedResult<UserSummary>>("/api/v1/users?includeInactive=true&pageSize=200", json))!.Items];
        Task<HttpResponseMessage> Get(Guid id) => client.GetAsync($"/api/v1/users/{id.ToBase58()}");
        async Task<UserDetail> Read(Guid id) => (await (await Get(id)).Content.ReadFromJsonAsync<UserDetail>(json))!;
        var teammate = concurrent.User.Id;
        var outsider = Base58.TryParseId(ownerId, out var parsed) ? parsed : Guid.Empty;

        As(invite.User.Id);
        Role("admin");
        var everyone = await Listed();
        check(everyone.Any(u => u.IsInvitationPending) && everyone.Any(u => !u.IsActive && !u.IsInvitationPending) &&
              (await Read(teammate)).LastSeenAt is not null,
            "Administrators see pending and deactivated accounts, and when someone was last here");

        Role("member");
        var forMember = await Listed();
        check(forMember.All(u => u.IsActive && !u.IsInvitationPending) && forMember.Any(u => u.Id == outsider),
            "Members see the active directory only, whatever includeInactive says");
        check((await Get(renewalRace.User.Id)).StatusCode == HttpStatusCode.NotFound,
            "A pending invitation is not found by a member");
        check((await Read(teammate)).LastSeenAt is null && (await Read(invite.User.Id)).LastSeenAt is not null,
            "Members are not told when someone else was last here, only themselves");

        Role("guest");
        var forGuest = await Listed();
        check(forGuest.Select(u => u.Id).Order().SequenceEqual(new[] { invite.User.Id, teammate }.Order()),
            "Guests see only themselves and the people they share a team with");
        check((await Get(outsider)).StatusCode == HttpStatusCode.NotFound && (await Get(teammate)).IsSuccessStatusCode,
            "A guest cannot look up someone outside their teams");

        Role("member");
        check((await client.PatchAsJsonAsync("/api/v1/me", new { avatarUrl = "https://tracker.example/pixel.png" })).StatusCode == HttpStatusCode.BadRequest,
            "Nobody points their own avatar at an address of their choosing");
        check((await client.PatchAsJsonAsync("/api/v1/me", new { displayName = "Renamed User" })).IsSuccessStatusCode,
            "The rest of a profile is still the person's own to edit");
        Role("admin");
        check((await Patch(invite, new { avatarUrl = "javascript:alert(1)" })).StatusCode == HttpStatusCode.BadRequest,
            "An administrator cannot store an avatar address a browser would not load as a picture");
        check((await Patch(invite, new { avatarUrl = "https://intranet.example/people/invited.png" })).IsSuccessStatusCode,
            "An administrator sets a person's avatar");
        Role("member");
        var cleared = await client.PatchAsJsonAsync("/api/v1/me", new { avatarUrl = (string?)null });
        check(cleared.IsSuccessStatusCode && (await cleared.Content.ReadFromJsonAsync<UserSummary>(json))!.AvatarUrl is null,
            "A person can take their own avatar down");

        // Deleting outright. `pending` was invited and never accepted, and is given a team membership;
        // `renewalRace` is pending too, and becomes the only lead of a second team.
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlannerDbContext>();
            var checks = await db.Teams.SingleAsync(t => t.Key == "CHK");
            var led = new Planner.Domain.Entities.Team { Key = "LED", Name = "Led" };
            db.Teams.Add(led);
            db.TeamMembers.Add(new Planner.Domain.Entities.TeamMember { TeamId = checks.Id, UserId = pending.User.Id });
            db.TeamMembers.Add(new Planner.Domain.Entities.TeamMember
            {
                TeamId = led.Id, UserId = renewalRace.User.Id, Role = Planner.Contracts.Enums.TeamRole.Lead
            });
            await db.SaveChangesAsync();
        }

        Task<HttpResponseMessage> Delete(Guid id) => client.DeleteAsync($"/api/v1/users/{id.ToBase58()}?permanent=true");
        check((await Delete(pending.User.Id)).StatusCode == HttpStatusCode.Forbidden,
            "Members cannot delete an account");
        Role("admin");
        check((await Delete(teammate)).StatusCode == HttpStatusCode.Conflict && (await Get(teammate)).IsSuccessStatusCode,
            "An account that has signed in cannot be deleted, only deactivated");
        check((await Delete(owner.User.Id)).StatusCode == HttpStatusCode.Forbidden,
            "Admins cannot delete an owner account");
        check((await Delete(renewalRace.User.Id)).StatusCode == HttpStatusCode.Conflict,
            "Deleting an account does not leave a team without a lead");
        check((await Delete(pending.User.Id)).StatusCode == HttpStatusCode.NoContent &&
              (await Get(pending.User.Id)).StatusCode == HttpStatusCode.NotFound,
            "An administrator deletes an account that never signed in");
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlannerDbContext>();
            check(!await db.TeamMembers.AnyAsync(m => m.UserId == pending.User.Id),
                "A deleted account's team memberships go with it");
        }
        check((await Create("renew@planner.test")).StatusCode == HttpStatusCode.Created,
            "A deleted account's email address can be invited again");
        Role("owner");
        check((await Delete(owner.User.Id)).StatusCode == HttpStatusCode.NoContent,
            "Owners can delete an owner account that never signed in");
    }

    private sealed class CheckAuthentication(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers["X-Check-Role"].ToString();
            if (string.IsNullOrEmpty(role)) return Task.FromResult(AuthenticateResult.NoResult());
            var subject = Request.Headers["X-Check-Sub"].ToString();
            if (string.IsNullOrEmpty(subject)) subject = "00000000-0000-0000-0000-000000000001";
            var identity = new ClaimsIdentity([new Claim("sub", subject), new Claim("role", role)], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
