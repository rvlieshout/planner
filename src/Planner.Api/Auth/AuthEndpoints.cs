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

        if (request.IsPasswordGrantType())
        {
            return await HandlePasswordGrantAsync(request, userManager, signInManager);
        }

        if (request.IsRefreshTokenGrantType())
        {
            return await HandleRefreshGrantAsync(context, userManager);
        }

        return Reject(Errors.UnsupportedGrantType, "Only the password and refresh_token grants are supported.");
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

    private static async Task<IResult> HandleRefreshGrantAsync(HttpContext context, UserManager<AppUser> userManager)
    {
        var authentication = await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        var subject = authentication.Principal?.GetClaim(Claims.Subject);

        var user = subject is null ? null : await userManager.FindByIdAsync(subject);

        if (user is null || !user.IsActive)
        {
            return Reject(Errors.InvalidGrant, "The account tied to this refresh token can no longer sign in.");
        }

        user.LastSeenAt = DateTimeOffset.UtcNow;
        await userManager.UpdateAsync(user);

        // Claims are rebuilt from the database rather than copied from the old token, so a role change
        // or a team removal takes effect at the next refresh instead of at the next sign-in.
        var scopes = authentication.Principal!.GetScopes();
        var principal = await BuildPrincipalAsync(user, userManager, scopes);

        return Results.SignIn(principal, null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static async Task<ClaimsPrincipal> BuildPrincipalAsync(
        AppUser user,
        UserManager<AppUser> userManager,
        ImmutableArray<string> requestedScopes)
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
            PlannerScopes.Api
        ]));

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
