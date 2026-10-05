using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenIddict.Abstractions;
using Planner.Api.Auth;
using Planner.Api.Authorization;
using Planner.Api.Common;
using Planner.Api.Endpoints;
using Planner.Api.Realtime;
using Planner.Contracts.Common;
using Planner.Contracts.Issues;
using Planner.Contracts.Settings;
using Planner.Domain.Entities;
using Planner.Domain.Identity;
using Planner.Infrastructure;
using Planner.Infrastructure.Seeding;

namespace Planner.Api.Checks;

/// <summary>What ends a session, and how much one account may store. Real tokens from the real token
/// endpoint, validated as the API validates them, against a database: set PLANNER_CHECKS_POSTGRES as
/// for <see cref="InvitationChecks"/>. Each run creates and drops only its own database.</summary>
public static class SessionChecks
{
    private const string Password = "a long chosen password";
    private const long DailyAllowance = 3000;

    public static async Task RunAsync(Action<bool, string> check)
    {
        var connectionString = Environment.GetEnvironmentVariable("PLANNER_CHECKS_POSTGRES");
        if (string.IsNullOrEmpty(connectionString))
        {
            Console.WriteLine("SKIP: Session integration checks require PLANNER_CHECKS_POSTGRES.");
            return;
        }

        var database = "planner_sessions_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(connectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin))
            await create.ExecuteNonQueryAsync();
        var files = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, database));
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Warning);
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Attachments:Path"] = Path.Combine(files.FullName, "attachments"),
                ["Attachments:DailyBytesPerUser"] = DailyAllowance.ToString()
            });
            builder.Services.AddPlannerPersistence(new NpgsqlConnectionStringBuilder(connectionString)
                { Database = database }.ConnectionString);
            var auth = new PlannerAuthOptions { KeyDirectory = files.FullName, AllowInsecureHttp = true };
            builder.Services.AddPlannerAuth(auth);
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
            builder.Services.AddBase58Ids();
            builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.AddIdConverters());
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddScoped<CurrentUser>();
            builder.Services.AddScoped<TeamAccess>();
            builder.Services.AddScoped<ActivityLog>();
            builder.Services.AddScoped<IssueCommands>();
            builder.Services.AddScoped<CommentCommands>();
            builder.Services.AddScoped<AttachmentCommands>();
            builder.Services.AddScoped<OpenIddictClientSeeder>();
            builder.Services.AddScoped<McpClientCleanup>();
            builder.Services.AddScoped<DatabaseSeeder>();
            builder.Services.Configure<PlannerSeedOptions>(options =>
            {
                options.OwnerEmail = "seeded-owner@planner.test";
                options.OwnerPassword = Password;
                options.SeedDemoData = true;
            });
            builder.Services.AddSignalR();
            builder.Services.AddScoped<RealtimeNotifier>();
            builder.Services.AddScoped<RealtimeSubscriptions>();
            builder.Services.AddSingleton<RealtimeConnections>();
            builder.Services.AddRateLimiter(options => options.AddPolicy("auth", _ => RateLimitPartition.GetNoLimiter("all")));
            await using var app = builder.Build();
            app.UseBase58Ids();
            app.UseAuthentication();
            app.UseLiveAccounts();
            app.UseRateLimiter();
            app.UseAuthorization();
            app.MapAuthEndpoints();
            app.MapUserEndpoints();
            app.MapIssueEndpoints();
            app.MapSettingsEndpoints();

            var people = new Dictionary<string, Guid>();
            Guid teamId;
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PlannerDbContext>();
                await db.Database.EnsureCreatedAsync();
                var roles = scope.ServiceProvider.GetRequiredService<RoleManager<AppRole>>();
                foreach (var role in PlannerRoles.All.Keys)
                    await roles.CreateAsync(new AppRole { Name = role });
                await scope.ServiceProvider.GetRequiredService<OpenIddictClientSeeder>().SeedAsync(auth);

                // Before anything else exists: demo data only goes into an empty database.
                await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedAsync();

                var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
                foreach (var (name, role) in new[]
                {
                    ("owner", PlannerRoles.Owner), ("admin", PlannerRoles.Admin), ("leaver", PlannerRoles.Member),
                    ("reset", PlannerRoles.Member), ("changer", PlannerRoles.Member), ("writer", PlannerRoles.Member),
                    ("uploader", PlannerRoles.Member), ("second", PlannerRoles.Admin)
                })
                {
                    var user = new AppUser
                    {
                        Id = Guid.CreateVersion7(), UserName = $"{name}@planner.test", Email = $"{name}@planner.test",
                        EmailConfirmed = true, DisplayName = name
                    };
                    if (!(await users.CreateAsync(user, Password)).Succeeded || !(await users.AddToRoleAsync(user, role)).Succeeded)
                        throw new InvalidOperationException($"Could not create {name}.");
                    people[name] = user.Id;
                }

                var team = new Team { Key = "CHK", Name = "Checks" };
                db.Teams.Add(team);
                db.WorkflowStates.AddRange(WorkflowStateDefaults.CreateFor(team.Id));
                db.TeamMembers.Add(new TeamMember { TeamId = team.Id, UserId = people["writer"] });
                db.TeamMembers.Add(new TeamMember { TeamId = team.Id, UserId = people["uploader"] });
                await db.SaveChangesAsync();
                teamId = team.Id;
            }

            await app.StartAsync();
            try
            {
                await CheckAsync(app, auth, people, teamId, check);
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
            files.Delete(recursive: true);
        }
    }

    private sealed record Session(string Access, string Refresh);

    private static async Task CheckAsync(
        WebApplication app, PlannerAuthOptions auth, Dictionary<string, Guid> people, Guid teamId, Action<bool, string> check)
    {
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        json.Converters.AddIdConverters();

        async Task<HttpResponseMessage> Token(Dictionary<string, string> form)
        {
            form["client_id"] = auth.WebClientId;
            return await client.PostAsync("/connect/token", new FormUrlEncodedContent(form));
        }
        async Task<Session> Read(HttpResponseMessage response)
        {
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Token request failed: {await response.Content.ReadAsStringAsync()}");
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            return new Session(body.GetProperty("access_token").GetString()!, body.GetProperty("refresh_token").GetString()!);
        }
        Task<HttpResponseMessage> Login(string name, string password = Password) => Token(new()
        {
            ["grant_type"] = "password", ["username"] = $"{name}@planner.test", ["password"] = password,
            ["scope"] = "offline_access planner.api"
        });
        Task<HttpResponseMessage> Refresh(Session session) =>
            Token(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = session.Refresh });
        Task<HttpResponseMessage> Send(Session session, HttpMethod method, string path, HttpContent? content = null)
        {
            var request = new HttpRequestMessage(method, path) { Content = content };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Access);
            return client.SendAsync(request);
        }
        Task<HttpResponseMessage> Json(Session session, HttpMethod method, string path, object body) =>
            Send(session, method, path, JsonContent.Create(body, options: json));
        async Task<HttpStatusCode> Me(Session session) => (await Send(session, HttpMethod.Get, "/api/v1/me")).StatusCode;
        string UserPath(string name) => $"/api/v1/users/{people[name].ToBase58()}";

        var owner = await Read(await Login("owner"));

        // An organisation role is read from the account, not from the token it was issued into.
        var demoted = await Read(await Login("admin"));
        check((await Json(demoted, HttpMethod.Patch, UserPath("writer"), new { displayName = "Writer" })).IsSuccessStatusCode,
            "An administrator's token administers");
        check((await Json(owner, HttpMethod.Patch, UserPath("admin"), new { role = "member" })).IsSuccessStatusCode,
            "The owner demotes an administrator");
        check((await Json(demoted, HttpMethod.Patch, UserPath("writer"), new { displayName = "Writer" })).StatusCode == HttpStatusCode.Forbidden,
            "A demoted administrator's token stops administering at once");
        check(await Me(demoted) == HttpStatusCode.OK && (await Refresh(demoted)).IsSuccessStatusCode,
            "A change of role does not end the session");

        // Deactivation.
        var leaver = await Read(await Login("leaver"));
        check(await Me(leaver) == HttpStatusCode.OK, "An active account's token is accepted");
        check((await Send(owner, HttpMethod.Delete, UserPath("leaver"))).IsSuccessStatusCode, "The owner deactivates an account");
        check(await Me(leaver) == HttpStatusCode.Unauthorized,
            "A deactivated account's still-valid access token is refused at once");
        check((await Refresh(leaver)).StatusCode == HttpStatusCode.BadRequest, "A deactivated account cannot refresh");

        // A password reset by an administrator.
        var reset = await Read(await Login("reset"));
        var refreshed = await Read(await Refresh(reset));
        check(await Me(refreshed) == HttpStatusCode.OK, "A refreshed session carries on working");
        check((await Json(owner, HttpMethod.Post, UserPath("reset") + "/password", new { newPassword = "a replacement password" })).StatusCode == HttpStatusCode.NoContent,
            "The owner resets a password");
        check(await Me(refreshed) == HttpStatusCode.Unauthorized,
            "A password reset ends the access tokens issued under the old password");
        check((await Refresh(refreshed)).StatusCode == HttpStatusCode.BadRequest,
            "A password reset ends the refresh tokens issued under the old password");
        check(await Me(await Read(await Login("reset", "a replacement password"))) == HttpStatusCode.OK,
            "Signing in with the new password starts a working session");

        // Changing one's own password.
        var changer = await Read(await Login("changer"));
        check((await Json(changer, HttpMethod.Post, "/api/v1/me/password",
                new { currentPassword = Password, newPassword = "another chosen password" })).StatusCode == HttpStatusCode.NoContent,
            "A person changes their own password");
        check(await Me(changer) == HttpStatusCode.Unauthorized && (await Refresh(changer)).StatusCode == HttpStatusCode.BadRequest,
            "Changing a password ends every session opened under the old one, this one included");

        // What must not end a session: a passkey being used, added or removed.
        var writer = await Read(await Login("writer"));
        using (var scope = app.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = (await users.FindByIdAsync(people["writer"].ToString()))!;
            var passkey = new UserPasskeyInfo([1, 2, 3, 4], [5, 6, 7, 8], DateTimeOffset.UtcNow, 1, null,
                isUserVerified: true, isBackupEligible: false, isBackedUp: false, attestationObject: [9], clientDataJson: [10]);
            var added = await users.AddOrUpdatePasskeyAsync(user, passkey);
            var removed = await users.RemovePasskeyAsync(user, [1, 2, 3, 4]);
            check(added.Succeeded && removed.Succeeded && await Me(writer) == HttpStatusCode.OK,
                "Adding, using or removing a passkey leaves existing sessions alone");
        }

        // How much one account may store.
        var createdIssue = await Json(writer, HttpMethod.Post, "/api/v1/issues", new { teamId, title = "Limits" });
        check(createdIssue.StatusCode == HttpStatusCode.Created, $"A member files an issue ({createdIssue.StatusCode})");
        var issue = (await createdIssue.Content.ReadFromJsonAsync<IssueSummary>(json))!;
        var issuePath = $"/api/v1/issues/{issue.Id.ToBase58()}";

        Task<HttpResponseMessage> Comment(int length) =>
            Json(writer, HttpMethod.Post, issuePath + "/comments", new { body = new string('a', length) });
        check((await Comment(TextLimits.Comment)).StatusCode == HttpStatusCode.Created &&
              (await Comment(TextLimits.Comment + 1)).StatusCode == HttpStatusCode.BadRequest,
            $"A comment may be {TextLimits.Comment:N0} characters and no longer");
        Task<HttpResponseMessage> Describe(int length) =>
            Json(writer, HttpMethod.Patch, issuePath, new { description = new string('a', length) });
        check((await Describe(TextLimits.Description)).IsSuccessStatusCode &&
              (await Describe(TextLimits.Description + 1)).StatusCode == HttpStatusCode.BadRequest,
            $"A description may be {TextLimits.Description:N0} characters and no longer");
        check((await Json(writer, HttpMethod.Post, "/api/v1/issues",
                new { teamId, title = "Too long", description = new string('a', TextLimits.Description + 1) })).StatusCode == HttpStatusCode.BadRequest,
            "An issue cannot be filed with a longer description either");

        Task<HttpResponseMessage> Upload(string name, int bytes) =>
            Send(writer, HttpMethod.Post, $"{issuePath}/files?fileName={name}", new ByteArrayContent(new byte[bytes]));
        var first = await Upload("first.bin", 2000);
        check(first.StatusCode == HttpStatusCode.Created, $"An upload inside the daily allowance is stored ({first.StatusCode})");
        check((await Upload("second.bin", 2000)).StatusCode == HttpStatusCode.TooManyRequests,
            "An upload that would pass the daily allowance is refused");
        check((await Upload("small.bin", 900)).StatusCode == HttpStatusCode.Created, "What is left of the allowance can still be used");
        var stored = (await first.Content.ReadFromJsonAsync<AttachmentDto>(json))!;
        check((await Send(writer, HttpMethod.Delete, $"/api/v1/attachments/{stored.Id.ToBase58()}")).IsSuccessStatusCode &&
              (await Upload("second.bin", 2000)).StatusCode == HttpStatusCode.Created,
            "Removing a file gives its share of the allowance back");
        var directory = Path.Combine(AppContext.BaseDirectory, new DirectoryInfo(auth.KeyDirectory).Name, "attachments");
        check(Directory.GetFiles(directory).Sum(file => new FileInfo(file).Length) == 2900,
            "A refused upload leaves nothing behind on disk");

        // The team's limit, which the owner sets. The team holds 2,900 bytes at this point, and the
        // uploader has a daily allowance of their own that nothing above has touched.
        var uploader = await Read(await Login("uploader"));
        async Task<T> Get<T>(Session session, string path) =>
            (await (await Send(session, HttpMethod.Get, path)).Content.ReadFromJsonAsync<T>(json))!;
        Task<HttpResponseMessage> Limit(Session session, long bytes) =>
            Json(session, HttpMethod.Patch, "/api/v1/settings", new { teamStorageBytes = bytes });
        Task<HttpResponseMessage> Add(string name, int bytes) =>
            Send(uploader, HttpMethod.Post, $"{issuePath}/files?fileName={name}", new ByteArrayContent(new byte[bytes]));
        var storagePath = $"/api/v1/teams/{teamId.ToBase58()}/storage";

        check((await Get<OrganizationSettingsDto>(writer, "/api/v1/settings")).TeamStorageBytes == 5L * 1024 * 1024 * 1024,
            "A team may hold 5 GiB of attachments until the owner says otherwise");
        check((await Limit(writer, 4000)).StatusCode == HttpStatusCode.Forbidden && (await Limit(demoted, 4000)).StatusCode == HttpStatusCode.Forbidden,
            "Only the owner changes the team storage limit");
        check((await Limit(owner, -1)).StatusCode == HttpStatusCode.BadRequest, "A negative limit is refused");
        check((await Limit(owner, 4000)).IsSuccessStatusCode &&
              (await Get<OrganizationSettingsDto>(writer, "/api/v1/settings")).TeamStorageBytes == 4000,
            "The owner sets the team storage limit");
        check((await Add("fits.bin", 1000)).StatusCode == HttpStatusCode.Created, "An upload that fits the team's limit is stored");
        check((await Add("over.bin", 200)).StatusCode == HttpStatusCode.Conflict,
            "An upload that would take the team past its limit is refused, whoever sends it");
        var storage = await Get<TeamStorageDto>(writer, storagePath);
        check(storage.UsedBytes == 3900 && storage.LimitBytes == 4000, "A team member can see how much of the limit is used");
        check((await Send(owner, HttpMethod.Get, storagePath)).StatusCode == HttpStatusCode.OK &&
              (await Send(owner, HttpMethod.Get, $"/api/v1/teams/{Guid.NewGuid().ToBase58()}/storage")).StatusCode == HttpStatusCode.NotFound,
            "An administrator sees any team's storage, and a team that does not exist is not found");
        check((await Send(changer = await Read(await Login("changer", "another chosen password")), HttpMethod.Get, storagePath)).StatusCode == HttpStatusCode.NotFound,
            "Someone outside the team is not told how much it stores");
        check((await Limit(owner, 0)).IsSuccessStatusCode && (await Get<TeamStorageDto>(writer, storagePath)).LimitBytes is null &&
              (await Add("over.bin", 200)).StatusCode == HttpStatusCode.Created,
            "A limit of 0 means no limit");

        // Roles: nothing half-applied, and never an installation without an owner.
        var second = await Read(await Login("second"));
        check((await Json(second, HttpMethod.Patch, UserPath("second"), new { role = "member", isActive = false })).StatusCode == HttpStatusCode.BadRequest &&
              (await Json(second, HttpMethod.Patch, UserPath("writer"), new { displayName = "Writer" })).IsSuccessStatusCode,
            "A refused update leaves the role as it was");
        using (var scope = app.Services.CreateScope())
        {
            // The seeder made an owner of its own; with it deactivated, `owner` is the only one left.
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var seeded = (await users.FindByEmailAsync("seeded-owner@planner.test"))!;
            seeded.IsActive = false;
            await users.UpdateAsync(seeded);
        }
        check((await Json(owner, HttpMethod.Patch, UserPath("owner"), new { role = "admin" })).StatusCode == HttpStatusCode.Conflict,
            "The only owner cannot give up the role");
        check((await Json(owner, HttpMethod.Patch, UserPath("second"), new { role = "owner" })).IsSuccessStatusCode &&
              (await Json(owner, HttpMethod.Patch, UserPath("owner"), new { role = "admin" })).IsSuccessStatusCode,
            "With a second owner in place, an owner can step down");

        // Demo accounts.
        var demoLogin = await Token(new()
        {
            ["grant_type"] = "password", ["username"] = "dana@planner.local", ["password"] = Password, ["scope"] = "planner.api"
        });
        using (var scope = app.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            check(await users.FindByEmailAsync("dana@planner.local") is not null && demoLogin.StatusCode == HttpStatusCode.BadRequest,
                "Demo accounts are created, and the owner's password does not open them");
        }

        // Self-registered MCP clients that nobody ever used are removed, in time.
        using (var scope = app.Services.CreateScope())
        {
            var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
            var cleanup = scope.ServiceProvider.GetRequiredService<McpClientCleanup>();
            var now = DateTimeOffset.UtcNow;

            async Task<object> Register(string clientId, DateTimeOffset? registered)
            {
                var descriptor = McpClients.Describe(clientId, clientId, [new Uri("https://client.example/callback")], null, dynamic: true);
                if (registered is { } at)
                    descriptor.Properties[McpClients.RegisteredProperty] = JsonSerializer.SerializeToElement(at.ToUnixTimeSeconds());
                else
                    descriptor.Properties.Remove(McpClients.RegisteredProperty);
                return await applications.CreateAsync(descriptor);
            }
            async Task<bool> Exists(string clientId) => await applications.FindByClientIdAsync(clientId) is not null;

            await Register("mcp-abandoned", now.AddDays(-40));
            await Register("mcp-recent", now.AddDays(-2));
            await Register("mcp-undated", null);
            var used = await Register("mcp-used", now.AddDays(-40));
            await tokens.CreateAsync(new OpenIddictTokenDescriptor
            {
                ApplicationId = await applications.GetIdAsync(used),
                Subject = people["writer"].ToString(),
                Type = OpenIddictConstants.TokenTypeHints.RefreshToken,
                Status = OpenIddictConstants.Statuses.Valid,
                CreationDate = now.AddDays(-39),
                ExpirationDate = now.AddDays(-9)
            });

            check(await cleanup.SweepAsync(now) == 1 && !await Exists("mcp-abandoned"),
                "A self-registered client unused for over 30 days is removed");
            check(await Exists("mcp-recent") && await Exists("mcp-undated") && await Exists("mcp-used") &&
                  await Exists(auth.McpClientId) && await Exists(auth.WebClientId),
                "Recent, undated, used and configured clients are kept");
            check(await cleanup.SweepAsync(now.AddDays(31)) == 2 && !await Exists("mcp-recent") && !await Exists("mcp-undated") &&
                  await Exists("mcp-used") && await Exists(auth.McpClientId),
                "An undated registration is dated on first sight and ages out like any other; a client someone used never does");
        }
    }
}
