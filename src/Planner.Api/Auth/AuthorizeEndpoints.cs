using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using Planner.Api.Authorization;
using Planner.Domain.Identity;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Planner.Api.Auth;

/// <summary>The authorization code flow, for third-party clients such as MCP servers' hosts.
///
/// The API has no sign-in page of its own: the web client already does passkeys and passwords, and is
/// served from this origin. So /connect/authorize sends the browser to the client's consent page, the
/// page records the user's decision here with its own bearer token, and the browser comes back to
/// /connect/authorize carrying a short-lived cookie that says who decided what for which client.</summary>
public static class AuthorizeEndpoints
{
    private const string ClientClaim = "planner:client_id";
    private const string DecisionClaim = "planner:decision";
    private const string ConsentPage = "/app/authorize";

    public sealed record ConsentRequest(string ClientId, bool Allow);

    /// <param name="Verified">False for a client that registered itself: its name is its own claim.</param>
    public sealed record AuthorizeClientResponse(string ClientId, string DisplayName, bool Verified);

    public static IEndpointRouteBuilder MapAuthorizeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/connect/authorize", AuthorizeAsync)
            .AllowAnonymous()
            .RequireRateLimiting("auth")
            .ExcludeFromDescription();

        app.MapGet("/connect/authorize/client", DescribeClientAsync)
            .RequireAuthorization()
            .WithName("AuthorizeClient")
            .WithSummary("Describe a client asking for access, for the consent screen")
            .WithTags("Auth");

        app.MapPost("/connect/authorize/consent", ConsentAsync)
            .RequireAuthorization()
            .RequireRateLimiting("auth")
            .WithName("AuthorizeConsent")
            .WithSummary("Record the signed-in user's decision on a client's access request")
            .WithTags("Auth");

        return app;
    }

    private static async Task<IResult> AuthorizeAsync(
        HttpContext context,
        UserManager<AppUser> users,
        SignInManager<AppUser> signInManager,
        IOptions<PlannerAuthOptions> auth)
    {
        // OpenIddict has already checked the client, its redirect_uri, the PKCE challenge, the scopes
        // and the resource against what the client is permitted, and answered any failure itself.
        var request = context.GetOpenIddictServerRequest()
                      ?? throw new InvalidOperationException("The OpenID Connect request is not available.");

        var consent = await context.AuthenticateAsync(AuthenticationSetup.AuthorizeCookieScheme);

        // A decision counts for the client it was made about and nothing else: approving one client
        // must not also approve whichever client the next link in this browser names. There is no
        // remembered consent, so every request is explicitly approved and prompt=consent is always met.
        var decided = consent.Succeeded
                      && consent.Principal.FindFirstValue(ClientClaim) == request.ClientId;

        if (!decided)
        {
            if (request.HasPromptValue(PromptValues.None))
            {
                return Forbid(Errors.ConsentRequired, "The user has to sign in and approve this client.");
            }

            var returnUrl = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
            return Results.Redirect(ConsentPage + "?return=" + Uri.EscapeDataString(returnUrl));
        }

        // Single use: going back to this URL has to go through the consent page again.
        await context.SignOutAsync(AuthenticationSetup.AuthorizeCookieScheme);

        var decision = consent.Principal!;

        if (decision.FindFirstValue(DecisionClaim) != "allow")
        {
            return Forbid(Errors.AccessDenied, "The user declined access.");
        }

        var user = await users.FindByIdAsync(decision.FindFirstValue(Claims.Subject) ?? string.Empty);

        if (user is null || !user.IsActive || !await signInManager.CanSignInAsync(user) ||
            await users.IsLockedOutAsync(user))
        {
            return Forbid(Errors.AccessDenied, "This account can no longer sign in.");
        }

        // A token from this flow is only ever for the MCP endpoint. OpenIddict has rejected unregistered
        // resources already; this also refuses a request that names none, which would otherwise mint an
        // audience-less token every endpoint accepts.
        var mcpResource = auth.Value.ResolveMcpResource()?.AbsoluteUri;
        var resources = request.GetResources();

        if (mcpResource is null || resources.Length != 1 || resources[0] != mcpResource)
        {
            return Forbid(Errors.InvalidTarget, "Tokens from this installation are issued for its MCP endpoint only.");
        }

        user.LastSeenAt = DateTimeOffset.UtcNow;
        await users.UpdateAsync(user);

        var principal = await AuthEndpoints.BuildPrincipalAsync(user, users, request.GetScopes(), resources);
        return Results.SignIn(principal, null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>Only what this server knows about the client, never what the authorization request
    /// claims. A client that registered itself chose its own name, so it comes back unverified and the
    /// consent screen says so.</summary>
    private static async Task<IResult> DescribeClientAsync(
        string clientId,
        IOpenIddictApplicationManager applications,
        CancellationToken ct)
    {
        var client = await FindCodeClientAsync(clientId, applications, ct);

        if (client is null)
        {
            return Results.NotFound();
        }

        var name = await applications.GetDisplayNameAsync(client, ct) ?? clientId;
        var verified = !await McpClients.IsDynamicAsync(applications, client, ct);
        return Results.Ok(new AuthorizeClientResponse(clientId, name, verified));
    }

    /// <summary>The bearer token requirement is what makes this safe from cross-site requests: a
    /// forged form post carries the cookie jar but not the web client's access token.</summary>
    private static async Task<IResult> ConsentAsync(
        ConsentRequest body,
        HttpContext context,
        CurrentUser user,
        IOpenIddictApplicationManager applications,
        CancellationToken ct)
    {
        if (await FindCodeClientAsync(body.ClientId, applications, ct) is null)
        {
            return Results.NotFound();
        }

        var identity = new ClaimsIdentity(AuthenticationSetup.AuthorizeCookieScheme);
        identity.AddClaim(new Claim(Claims.Subject, user.Id.ToString()));
        identity.AddClaim(new Claim(ClientClaim, body.ClientId));
        identity.AddClaim(new Claim(DecisionClaim, body.Allow ? "allow" : "deny"));

        await context.SignInAsync(AuthenticationSetup.AuthorizeCookieScheme, new ClaimsPrincipal(identity));
        return Results.NoContent();
    }

    /// <summary>A client that may use the code flow. The first-party clients may not, so their ids are
    /// "not found" here as well.</summary>
    private static async Task<object?> FindCodeClientAsync(
        string clientId,
        IOpenIddictApplicationManager applications,
        CancellationToken ct)
    {
        var client = await applications.FindByClientIdAsync(clientId, ct);

        return client is not null &&
               await applications.HasPermissionAsync(client, Permissions.GrantTypes.AuthorizationCode, ct)
            ? client
            : null;
    }

    /// <summary>Hands the error to OpenIddict, which sends it back to the client's redirect_uri.</summary>
    private static IResult Forbid(string error, string description) =>
        Results.Forbid(
            new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
                [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description
            }),
            [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
}
