using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Server;
using Planner.Api.Auth;
using Planner.Api.Mcp;
using Planner.Infrastructure;

namespace Planner.Api.Checks;

/// <summary>The MCP endpoint's front door, with the production auth and MCP registrations and no
/// database: discovery, the challenge, which tokens get in where, and the tool list.</summary>
public static class McpEndpointChecks
{
    private const string Issuer = "https://planner.test/";
    private const string Resource = "https://planner.test/mcp";

    public static async Task RunAsync(Action<bool, string> check)
    {
        var keys = Directory.CreateTempSubdirectory("planner-mcp-endpoint-checks-");
        try
        {
            var auth = new PlannerAuthOptions { Issuer = Issuer, KeyDirectory = keys.FullName, AllowInsecureHttp = true };
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddPlannerPersistence("Host=localhost;Database=unused");
            builder.Services.AddPlannerAuth(auth);
            builder.Services.AddPlannerMcp(auth);
            var inactive = Guid.NewGuid();
            builder.Services.AddSingleton<IActiveAccounts>(new FixedAccounts(inactive));
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddRateLimiter(o => o.AddPolicy("mcp", _ => RateLimitPartition.GetNoLimiter("all")));
            await using var app = builder.Build();
            app.UseRateLimiter();
            app.UseAuthentication();
            app.UseMcpAudienceBoundary(auth);
            app.UseAuthorization();
            app.MapGet("/api/check", () => Results.Ok());
            app.MapPlannerMcp();
            await app.StartAsync();
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
                var options = app.Services.GetRequiredService<IOptionsMonitor<OpenIddictServerOptions>>().CurrentValue;

                string Token(string? audience, Guid? subject = null)
                {
                    var claims = new Dictionary<string, object> { ["sub"] = (subject ?? Guid.NewGuid()).ToString() };
                    if (audience is not null) claims["aud"] = audience;
                    return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
                    {
                        Issuer = Issuer,
                        TokenType = "at+jwt",
                        Claims = claims,
                        Expires = DateTime.UtcNow.AddMinutes(5),
                        SigningCredentials = options.SigningCredentials[0]
                    });
                }

                using (var anonymous = await Rpc(client, null, "initialize", Initialize))
                {
                    var challenge = anonymous.Headers.WwwAuthenticate.ToString();
                    check(anonymous.StatusCode == HttpStatusCode.Unauthorized && challenge.Contains("resource_metadata="),
                        "An anonymous MCP request is challenged with a pointer to the resource metadata");

                    var metadataUrl = challenge.Split("resource_metadata=\"")[1].Split('"')[0];
                    using var metadata = JsonDocument.Parse(await client.GetStringAsync(new Uri(metadataUrl).PathAndQuery));
                    var root = metadata.RootElement;
                    check(root.GetProperty("resource").GetString() == Resource,
                        "Resource metadata names the MCP endpoint as the resource");
                    check(root.GetProperty("authorization_servers").EnumerateArray().Any(s => s.GetString() == Issuer),
                        "Resource metadata names Planner as the authorization server");
                }

                using (var firstParty = await Rpc(client, Token(null), "initialize", Initialize))
                {
                    check(firstParty.StatusCode == HttpStatusCode.Forbidden,
                        "A token without the MCP audience (a web client token) is refused at /mcp");
                }

                var mcp = Token(Resource);
                using (var api = new HttpRequestMessage(HttpMethod.Get, "/api/check"))
                {
                    api.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mcp);
                    using var response = await client.SendAsync(api);
                    check(response.StatusCode == HttpStatusCode.Forbidden,
                        "An MCP token is refused by the REST API: it cannot reach the user's write access");
                }

                using (var deactivated = await Rpc(client, Token(Resource, inactive), "initialize", Initialize))
                {
                    check(deactivated.StatusCode == HttpStatusCode.Forbidden,
                        "A deactivated account's still-valid MCP token is refused at once");
                }

                using (var initialized = await Rpc(client, mcp, "initialize", Initialize))
                {
                    check(initialized.StatusCode == HttpStatusCode.OK, "An MCP token initializes a session at /mcp");
                }

                var tools = (await Result(await Rpc(client, mcp, "tools/list", new { })))
                    .GetProperty("tools").EnumerateArray().ToList();
                var names = tools.Select(t => t.GetProperty("name").GetString()).ToHashSet();
                string[] expected =
                [
                    "list_teams", "list_projects", "get_project", "search_issues", "get_issue", "get_inbox",
                    "get_activity", "team_digest"
                ];
                check(expected.All(names.Contains), "Every tool is listed: " + string.Join(", ", expected));
                check(tools.All(t => t.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean()),
                    "Every tool is annotated read-only");

                var prompts = await Result(await Rpc(client, mcp, "prompts/list", new { }));
                check(prompts.GetProperty("prompts").EnumerateArray().Any(p => p.GetProperty("name").GetString() == "team_summary"),
                    "The team_summary prompt is listed");
            }
            finally
            {
                await app.StopAsync();
            }
        }
        finally
        {
            keys.Delete(recursive: true);
        }
    }

    private sealed class FixedAccounts(Guid inactive) : IActiveAccounts
    {
        public Task<bool> IsActiveAsync(Guid userId, CancellationToken ct) => Task.FromResult(userId != inactive);
    }

    private static readonly object Initialize = new
    {
        protocolVersion = "2025-06-18",
        capabilities = new { },
        clientInfo = new { name = "planner-checks", version = "1.0" }
    };

    private static int _id;

    private static async Task<HttpResponseMessage> Rpc(HttpClient client, string? token, string method, object parameters)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0", id = Interlocked.Increment(ref _id), method, @params = parameters
                }),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", "2025-06-18");

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request);
    }

    /// <summary>The JSON-RPC result, from either a JSON body or a server-sent event stream.</summary>
    private static async Task<JsonElement> Result(HttpResponseMessage response)
    {
        using var _ = response;
        var body = await response.Content.ReadAsStringAsync();
        var json = body.TrimStart().StartsWith('{')
            ? body
            : string.Join("", body.Split('\n').Where(l => l.StartsWith("data:")).Select(l => l[5..].Trim()));

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("result", out var result))
        {
            throw new InvalidOperationException($"{(int)response.StatusCode}: {body}");
        }

        return result.Clone();
    }
}
