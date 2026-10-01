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
            builder.Services.AddScoped<IRealtimeNotifier, RealtimeNotifier>();
            builder.Services.AddScoped<IRealtimeSubscriptions, RealtimeSubscriptions>();
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
        check((await Create()).StatusCode == HttpStatusCode.BadRequest, "Duplicate invitation email is rejected");
        check((await client.PatchAsJsonAsync($"/api/v1/users/{invite.User.Id.ToBase58()}", new { isActive = true })).StatusCode == HttpStatusCode.BadRequest,
            "Admin activation cannot bypass invitation acceptance");
        check((await client.PostAsJsonAsync($"/api/v1/users/{invite.User.Id.ToBase58()}/password",
            new { newPassword = "a password by admin" })).StatusCode == HttpStatusCode.BadRequest,
            "Admin password reset cannot bypass invitation acceptance");

        Role(null);
        check((await Login(invite.User.Email, "a long chosen password")).StatusCode == HttpStatusCode.BadRequest,
            "Pending accounts cannot sign in");
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
        var pending = await ReadInvite(await Create("renew@planner.test"));
        var renewed = await ReadInvite(await Renew(pending));
        check((await Inspect(pending)).StatusCode == HttpStatusCode.BadRequest && (await Inspect(renewed)).IsSuccessStatusCode,
            "Renewal invalidates old links and supplies a usable replacement");
        check((await client.DeleteAsync($"/api/v1/users/{pending.User.Id.ToBase58()}")).IsSuccessStatusCode &&
              (await Inspect(renewed)).StatusCode == HttpStatusCode.BadRequest, "Deactivation cancels a pending invitation");
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
    }

    private sealed class CheckAuthentication(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers["X-Check-Role"].ToString();
            if (string.IsNullOrEmpty(role)) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity(
                [new Claim("sub", "00000000-0000-0000-0000-000000000001"), new Claim("role", role)], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
