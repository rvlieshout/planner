using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Planner.Api.Auth;
using Planner.Infrastructure;

namespace Planner.Api.Checks;

/// <summary>What an MCP client learns before it ever talks to a database: that this server does the
/// code flow, insists on S256, and which resource its tokens are for.</summary>
public static class McpAuthorizeChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        check(new PlannerAuthOptions { Issuer = "https://planner.test/" }.ResolveMcpResource()?.AbsoluteUri
              == "https://planner.test/mcp", "The MCP resource defaults to /mcp under the issuer");
        check(new PlannerAuthOptions { Issuer = "https://planner.test/sub" }.ResolveMcpResource()?.AbsoluteUri
              == "https://planner.test/sub/mcp", "An issuer without a trailing slash keeps its path");
        check(new PlannerAuthOptions { Issuer = "https://planner.test/", McpResource = "https://mcp.test/" }
                  .ResolveMcpResource()?.AbsoluteUri == "https://mcp.test/", "An explicit MCP resource wins");
        check(new PlannerAuthOptions().ResolveMcpResource() is null,
            "Without an issuer or resource there is no MCP audience to issue tokens for");

        foreach (var (uri, ok) in new[]
                 {
                     ("http://localhost:6274/oauth/callback", true),
                     ("http://127.0.0.1:53682/callback", true),
                     ("https://claude.ai/api/mcp/auth_callback", true),
                     ("com.example.assistant:/callback", true),
                     ("http://evil.example/callback", false),
                     ("javascript:alert(1)", false),
                     ("data:text/html,hi", false),
                     ("file:///etc/passwd", false),
                     ("https://claude.ai/cb#fragment", false),
                     ("/relative/callback", false)
                 })
        {
            check(RegistrationEndpoints.IsAcceptableRedirect(uri, out _) == ok,
                $"Registration {(ok ? "accepts" : "refuses")} redirect URI {uri}");
        }

        var keys = Directory.CreateTempSubdirectory("planner-mcp-checks-");
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddPlannerPersistence("Host=localhost;Database=unused");
            builder.Services.AddPlannerAuth(new PlannerAuthOptions
            {
                Issuer = "https://planner.test/",
                KeyDirectory = keys.FullName,
                AllowInsecureHttp = true
            });
            await using var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            await app.StartAsync();
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
                using var discovery = JsonDocument.Parse(await client.GetStringAsync("/.well-known/openid-configuration"));
                var root = discovery.RootElement;
                string[] Values(string name) => [.. root.GetProperty(name).EnumerateArray().Select(e => e.GetString()!)];

                check(new Uri(root.GetProperty("authorization_endpoint").GetString()!).AbsolutePath == "/connect/authorize",
                    "Discovery advertises the authorization endpoint");
                check(Values("grant_types_supported").Contains("authorization_code"),
                    "Discovery advertises the authorization code grant");
                check(Values("response_types_supported").SequenceEqual(["code"]),
                    "Only the code response type is offered: no implicit or hybrid tokens in URLs");
                check(Values("code_challenge_methods_supported").SequenceEqual(["S256"]),
                    "PKCE is offered with S256 only");
                check(Values("scopes_supported").Contains("planner.mcp"), "Discovery lists the MCP scope");

                check(new Uri(root.GetProperty("registration_endpoint").GetString()!).AbsolutePath == "/connect/register",
                    "Discovery advertises the dynamic client registration endpoint");

                using var oauth = await client.GetAsync("/.well-known/oauth-authorization-server");
                check(oauth.IsSuccessStatusCode,
                    "OAuth authorization server metadata (RFC 8414) is served too, for MCP clients that look there");
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
}
