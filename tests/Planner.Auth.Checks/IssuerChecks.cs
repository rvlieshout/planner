using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Server;
using Planner.Api.Auth;
using Planner.Api.Common;
using Planner.Infrastructure;

namespace Planner.Api.Checks;

public static class IssuerChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var keys = Directory.CreateTempSubdirectory("planner-issuer-checks-");
        try
        {
            // Use the production authentication registration, with no database operations.
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
            builder.Services.AddSignalR();
            await using var app = builder.Build();
            app.UseMiddleware<SignalRAuthenticationMiddleware>("/hubs");
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapGet("/api/check", () => Results.Ok());
            app.MapHub<IssuerCheckHub>("/hubs/planner").RequireAuthorization();
            await app.StartAsync();
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
                using var discovery = JsonDocument.Parse(await client.GetStringAsync("/.well-known/openid-configuration"));
                check(discovery.RootElement.GetProperty("issuer").GetString() == "https://planner.test/",
                    "Discovery keeps the public issuer when reached via an internal HTTP origin");

                var options = app.Services.GetRequiredService<IOptionsMonitor<OpenIddictServerOptions>>().CurrentValue;
                string Token(string issuer) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
                {
                    Issuer = issuer,
                    TokenType = "at+jwt",
                    Claims = new Dictionary<string, object> { ["sub"] = "test-user" },
                    Expires = DateTime.UtcNow.AddMinutes(5),
                    SigningCredentials = options.SigningCredentials[0]
                });
                var token = Token("https://planner.test/");
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var api = await client.GetAsync("/api/check");
                check(api.StatusCode == HttpStatusCode.OK, "Public-issuer token authenticates over internal HTTP");
                using var negotiation = await client.PostAsync("/hubs/planner/negotiate?negotiateVersion=1", null);
                check(negotiation.StatusCode == HttpStatusCode.OK, "SignalR negotiation accepts the same public-issuer token");

                using var socket = new ClientWebSocket();
                var uri = new UriBuilder(client.BaseAddress)
                {
                    Scheme = "ws", Path = "/hubs/planner", Query = "access_token=" + Uri.EscapeDataString(token)
                };
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await socket.ConnectAsync(uri.Uri, timeout.Token);
                check(socket.State == WebSocketState.Open, "WebSocket query-token authentication accepts the public issuer over an internal origin");
                socket.Abort();

                client.DefaultRequestHeaders.Authorization = null;
                using var rejected = await client.GetAsync("/hubs/planner?access_token=" + Uri.EscapeDataString(Token("https://other.test/")));
                check(rejected.StatusCode == HttpStatusCode.Unauthorized &&
                    rejected.Headers.WwwAuthenticate.ToString().Contains("ID2088"),
                    "A correctly signed token from a different issuer is still rejected");
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

public sealed class IssuerCheckHub : Hub;
