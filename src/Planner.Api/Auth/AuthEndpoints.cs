using System.Collections.Immutable;
using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using Planner.Domain.Identity;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Planner.Api.Auth;

/// <summary>OAuth 2.0 / OpenID Connect endpoints. OpenIddict validates the protocol shape of every
/// request before these handlers run; what is left here is deciding whether the credentials are good
/// and which claims the resulting token carries.</summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/connect/token", ExchangeAsync)
            .AllowAnonymous()
            .RequireRateLimiting("auth")
            .WithName("Token")
            .WithSummary("Exchange credentials or a refresh token for an access token")
            .WithTags("Auth");

        app.MapMethods("/connect/userinfo", ["GET", "POST"], UserInfoAsync)
            .RequireAuthorization()
            .WithName("UserInfo")
            .WithSummary("Claims about the authenticated subject")
            .WithTags("Auth");

        return app;
    }

    private static async Task<IResult> ExchangeAsync(
        HttpContext context,
        UserManager<AppUser> userManager,
        SignInManager<AppUser> signInManager)
    {
        var request = context.GetOpenIddictServerRequest()
                      ?? throw new InvalidOperationException("The OpenID Connect request is not available.");

        if (request.GrantType == PasskeyEndpoints.GrantType)
        {
            return await HandlePasskeyGrantAsync(context, request, userManager, signInManager);
        }

        if (request.IsPasswordGrantType())
        {
            return await HandlePasswordGrantAsync(request, userManager, signInManager);
        }

        // A code and a refresh token are both grants OpenIddict issued and has already validated
        // (including the PKCE verifier); what remains is re-checking the account behind them.
        if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
        {
            return await HandleIssuedGrantAsync(context, userManager);
        }

        return Reject(Errors.UnsupportedGrantType, "The requested sign-in method is not supported.");
    }

    private static async Task<IResult> HandlePasskeyGrantAsync(HttpContext context,
        OpenIddictRequest request, UserManager<AppUser> users, SignInManager<AppUser> signInManager)
    {
        var ceremonies = context.RequestServices.GetRequiredService<PasskeyCeremonies>();
        var state = ceremonies.Take(context, "login");
        var credential = (string?)request.GetParameter("credential");
        if (state is null || string.IsNullOrWhiteSpace(credential) || credential.Length > 65536)
            return Reject(Errors.InvalidGrant, "Passkey sign-in expired. Please try again.");
        var handler = context.RequestServices.GetRequiredService<IPasskeyHandler<AppUser>>();
        var result = await handler.PerformAssertionAsync(new PasskeyAssertionContext
        {
            HttpContext = context, CredentialJson = credential, AssertionState = state
        });
        if (!result.Succeeded || !result.User.IsActive ||
            !await signInManager.CanSignInAsync(result.User) || await users.IsLockedOutAsync(result.User))
            return Reject(Errors.InvalidGrant, "The passkey could not sign in to an active account.");
        // Assertion updates the authenticator counter and backup flags; persist before issuing tokens.
        if (!(await users.AddOrUpdatePasskeyAsync(result.User, result.Passkey)).Succeeded)
            return Reject(Errors.InvalidGrant, "The passkey could not be updated. Please try again.");
        result.User.LastSeenAt = DateTimeOffset.UtcNow;
        if (!(await users.UpdateAsync(result.User)).Succeeded)
            return Reject(Errors.InvalidGrant, "The account could not be updated. Please try again.");
        var principal = await BuildPrincipalAsync(result.User, users, request.GetScopes());
        return Results.SignIn(principal, null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static async Task<IResult> HandlePasswordGrantAsync(
        OpenIddictRequest request,
        UserManager<AppUser> userManager,
        SignInManager<AppUser> signInManager)
    {
        var user = await userManager.FindByEmailAsync(request.Username ?? string.Empty)
                   ?? await userManager.FindByNameAsync(request.Username ?? string.Empty);

        // One message for "no such user" and "wrong password" — a different answer per case turns the
        // token endpoint into an account-enumeration oracle.
        if (user is null)
        {
            return Reject(Errors.InvalidGrant, "The username or password is incorrect.");
        }

        if (!user.IsActive)
        {
            return Reject(Errors.InvalidGrant, "This account has been deactivated.");
        }

        var result = await signInManager.CheckPasswordSignInAsync(user, request.Password ?? string.Empty, lockoutOnFailure: true);

        if (result.IsLockedOut)
        {
            return Reject(Errors.InvalidGrant, "This account is temporarily locked after too many failed attempts.");
        }

        if (!result.Succeeded)
        {
            return Reject(Errors.InvalidGrant, "The username or password is incorrect.");
        }

        user.LastSeenAt = DateTimeOffset.UtcNow;
        await userManager.UpdateAsync(user);

        var principal = await BuildPrincipalAsync(user, userManager, request.GetScopes());
        return Results.SignIn(principal, null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static async Task<IResult> HandleIssuedGrantAsync(HttpContext context, UserManager<AppUser> userManager)
    {
        var authentication = await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        var subject = authentication.Principal?.GetClaim(Claims.Subject);

        var user = subject is null ? null : await userManager.FindByIdAsync(subject);

        if (user is null || !user.IsActive)
        {
            return Reject(Errors.InvalidGrant, "The account tied to this grant can no longer sign in.");
        }

        user.LastSeenAt = DateTimeOffset.UtcNow;
        await userManager.UpdateAsync(user);

        // Claims are rebuilt from the database rather than copied from the old token, so a role change
        // or a team removal takes effect at the next refresh instead of at the next sign-in. Scopes and
        // audience are what the user consented to, so those carry over unchanged.
        var principal = await BuildPrincipalAsync(user, userManager,
            authentication.Principal!.GetScopes(), authentication.Principal!.GetResources());

        return Results.SignIn(principal, null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    internal static async Task<ClaimsPrincipal> BuildPrincipalAsync(
        AppUser user,
        UserManager<AppUser> userManager,
        ImmutableArray<string> requestedScopes,
        ImmutableArray<string> resources = default)
    {
        var identity = new ClaimsIdentity(
            TokenValidationParameters.DefaultAuthenticationType,
            Claims.Name,
            Claims.Role);

        identity.SetClaim(Claims.Subject, user.Id.ToString())
            .SetClaim(Claims.Email, user.Email)
            .SetClaim(Claims.Name, user.DisplayName)
            .SetClaim(Claims.PreferredUsername, user.UserName);

        var roles = await userManager.GetRolesAsync(user);
        identity.SetClaims(Claims.Role, [.. roles]);

        var principal = new ClaimsPrincipal(identity);

        // offline_access is what makes OpenIddict issue a refresh token, so a client that asked for it
        // keeps it; everything else is intersected with what this server actually knows about.
        principal.SetScopes(requestedScopes.Intersect(
        [
            Scopes.OpenId,
            Scopes.Email,
            Scopes.Profile,
            Scopes.Roles,
            Scopes.OfflineAccess,
            PlannerScopes.Api,
            PlannerScopes.Mcp
        ]));

        // Becomes the token's audience. Only the authorization endpoint sets one, after checking it.
        if (!resources.IsDefaultOrEmpty)
        {
            principal.SetResources(resources);
        }

        principal.SetDestinations(AuthenticationSetup.GetDestinations);
        return principal;
    }

    private static async Task<IResult> UserInfoAsync(HttpContext context, UserManager<AppUser> userManager)
    {
        var subject = context.User.GetClaim(Claims.Subject);
        var user = subject is null ? null : await userManager.FindByIdAsync(subject);

        if (user is null)
        {
            return Results.Challenge(
                new AuthenticationProperties(new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidToken,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] =
                        "The access token is no longer associated with a user account."
                }),
                [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
        }

        var claims = new Dictionary<string, object>
        {
            [Claims.Subject] = user.Id.ToString(),
            [Claims.Name] = user.DisplayName,
            [Claims.PreferredUsername] = user.UserName ?? string.Empty,
            [Claims.Email] = user.Email ?? string.Empty,
            [Claims.EmailVerified] = user.EmailConfirmed,
            [Claims.Role] = await userManager.GetRolesAsync(user)
        };

        return Results.Ok(claims);
    }

    private static IResult Reject(string error, string description) =>
        Results.Forbid(
            new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
                [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description
            }),
            [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
}
