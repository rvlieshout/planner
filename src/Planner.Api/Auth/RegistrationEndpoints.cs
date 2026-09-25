using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using Planner.Contracts.Common;

namespace Planner.Api.Auth;

/// <summary>OAuth 2.0 Dynamic Client Registration (RFC 7591), for MCP clients.
///
/// MCP clients (the Inspector, Claude Code, IDE assistants) do not ship with a client id for every
/// server they might meet; they register one on first contact. What they can register is fixed: a
/// public client with the code flow, PKCE, refresh tokens and the MCP scope and resource, exactly what
/// the configured planner-mcp client has. Registering grants nothing by itself: every token still takes
/// a signed-in user approving that client by name on the consent page, which marks self-registered
/// names as unverified.</summary>
public static class RegistrationEndpoints
{
    private const int MaxRedirectUris = 5;
    private const int MaxNameLength = 100;

    public static IEndpointRouteBuilder MapRegistrationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/connect/register", RegisterAsync)
            .AllowAnonymous()
            .RequireRateLimiting("auth")
            .WithName("RegisterClient")
            .WithSummary("Register an MCP client (RFC 7591)")
            .WithTags("Auth");

        return app;
    }

    private static async Task<IResult> RegisterAsync(
        HttpContext context,
        IOpenIddictApplicationManager applications,
        IOptions<PlannerAuthOptions> options,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var auth = options.Value;

        if (!auth.AllowDynamicClientRegistration)
        {
            return Error("invalid_client_metadata", "This installation does not accept client registrations.",
                StatusCodes.Status403Forbidden);
        }

        JsonElement metadata;
        try
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: ct);
            metadata = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Error("invalid_client_metadata", "The registration request must be a JSON object.");
        }

        if (metadata.ValueKind != JsonValueKind.Object)
        {
            return Error("invalid_client_metadata", "The registration request must be a JSON object.");
        }

        // Redirect URIs are the one thing that decides where codes go, so they are held to RFC 8252:
        // HTTPS anywhere, plain HTTP only back to this machine, or an app's own reverse-DNS scheme.
        if (!metadata.TryGetProperty("redirect_uris", out var uris) || uris.ValueKind != JsonValueKind.Array ||
            uris.GetArrayLength() is 0 or > MaxRedirectUris)
        {
            return Error("invalid_redirect_uri", $"Register between 1 and {MaxRedirectUris} redirect_uris.");
        }

        var redirectUris = new List<Uri>();
        foreach (var item in uris.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !IsAcceptableRedirect(item.GetString(), out var uri))
            {
                return Error("invalid_redirect_uri",
                    $"'{item}' is not an acceptable redirect URI. Use https, http to localhost, or a private-use scheme such as com.example.app.");
            }

            redirectUris.Add(uri);
        }

        // Only what the MCP client definition allows. Asking for more is refused rather than quietly
        // trimmed, so a client never believes it holds a grant it does not.
        string[] allowedGrants = [OpenIddictConstants.GrantTypes.AuthorizationCode, OpenIddictConstants.GrantTypes.RefreshToken];
        if (Strings(metadata, "grant_types") is { } grants && grants.Except(allowedGrants).Any())
        {
            return Error("invalid_client_metadata", "Only the authorization_code and refresh_token grants can be registered.");
        }

        if (Strings(metadata, "response_types") is { } responses && responses.Except([OpenIddictConstants.ResponseTypes.Code]).Any())
        {
            return Error("invalid_client_metadata", "Only the code response type can be registered.");
        }

        var name = Clean(metadata.TryGetProperty("client_name", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString()
            : null) ?? "Unnamed MCP client";

        // Random, not sequential: a client id is not a secret, but it should not be guessable either.
        var clientId = "mcp-" + Guid.NewGuid().ToBase58();
        var descriptor = McpClients.Describe(clientId, name, redirectUris, auth.ResolveMcpResource(), dynamic: true);
        await applications.CreateAsync(descriptor, ct);

        loggers.CreateLogger("Planner.Auth.Registration").LogInformation(
            "Registered MCP client {ClientId} ({ClientName}) for {RedirectUris}", clientId, name, redirectUris);

        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(new Dictionary<string, object>
        {
            ["client_id"] = clientId,
            ["client_id_issued_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["client_name"] = name,
            ["redirect_uris"] = redirectUris.Select(u => u.OriginalString).ToArray(),
            ["grant_types"] = allowedGrants,
            ["response_types"] = new[] { OpenIddictConstants.ResponseTypes.Code },
            // Public, whatever was asked for: a client that registers itself from a user's machine has
            // nowhere safe to keep a secret. PKCE is what protects its codes.
            ["token_endpoint_auth_method"] = "none",
            ["scope"] = $"{PlannerScopes.Mcp} {OpenIddictConstants.Scopes.OfflineAccess}"
        }, statusCode: StatusCodes.Status201Created);
    }

    public static bool IsAcceptableRedirect(string? value, out Uri uri)
    {
        uri = null!;

        if (string.IsNullOrWhiteSpace(value) || value.Length > 2000 ||
            !Uri.TryCreate(value, UriKind.Absolute, out var parsed) || !string.IsNullOrEmpty(parsed.Fragment))
        {
            return false;
        }

        var acceptable = parsed.Scheme switch
        {
            "https" => true,
            "http" => parsed.IsLoopback,
            // RFC 8252 7.1: a private-use scheme is the app's reverse domain name, e.g. com.example.app.
            // Anything without a dot (javascript, data, file, ...) is not an app's own scheme.
            _ => parsed.Scheme.Contains('.')
        };

        uri = parsed;
        return acceptable;
    }

    private static string[]? Strings(JsonElement metadata, string property) =>
        metadata.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!)]
            : null;

    /// <summary>The name is shown on the consent screen: printable characters only, and short.</summary>
    private static string? Clean(string? name)
    {
        if (name is null)
        {
            return null;
        }

        var printable = new string([.. name.Where(c => !char.IsControl(c))]).Trim();
        return printable.Length == 0 ? null
            : printable.Length > MaxNameLength ? printable[..MaxNameLength]
            : printable;
    }

    private static IResult Error(string error, string description, int status = StatusCodes.Status400BadRequest) =>
        Results.Json(new Dictionary<string, string> { ["error"] = error, ["error_description"] = description },
            statusCode: status);
}
