using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Validation.AspNetCore;
using Planner.Infrastructure;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Planner.Api.Auth;

/// <summary>Ties a token to the state of the password it was issued under.
///
/// Identity replaces an account's security stamp whenever its password changes. Every token carries a
/// digest of the stamp that was current when it was issued, so a token from before a password change
/// or reset stops matching the account and is refused, whether it is an access token, a refresh token
/// or one an assistant holds. A digest, because the stamp itself is key material for Identity's own
/// tokens and access tokens are readable by whoever holds them.</summary>
public static class SessionStamp
{
    public const string ClaimType = "planner:stamp";

    public static string? Of(string? securityStamp) => string.IsNullOrEmpty(securityStamp)
        ? null
        : WebEncoders.Base64UrlEncode(
            SHA256.HashData(Encoding.UTF8.GetBytes("planner-session:" + securityStamp)).AsSpan(0, 12));

    /// <summary>A token issued before tokens carried a stamp has none. It is let through, and gets one
    /// the next time it is refreshed, rather than signing everyone out on the day of the upgrade.</summary>
    public static bool Matches(ClaimsPrincipal principal, string? securityStamp) =>
        principal.FindFirst(ClaimType)?.Value is not { } issuedUnder || issuedUnder == Of(securityStamp);
}

public static class LiveAccounts
{
    /// <summary>Checks the account behind every bearer token against the database, once per request.
    ///
    /// An access token is valid for as long as it says it is, and until this ran that was the whole
    /// story: someone deactivated at nine could keep working until ten, and an administrator demoted to
    /// member kept administering. Here a deactivated or deleted account, or a token from before a
    /// password change, is answered 401, which a client takes as "refresh", and the refresh is then
    /// refused for the same reason. The organisation role is replaced with the one the account holds
    /// now, so every policy and permission check downstream decides on that rather than on the token's.
    ///
    /// Goes after authentication and before anything that reads the caller's role.</summary>
    public static IApplicationBuilder UseLiveAccounts(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.User.Identity is not ClaimsIdentity { IsAuthenticated: true } identity)
            {
                await next(context);
                return;
            }

            var db = context.RequestServices.GetRequiredService<PlannerDbContext>();

            var account = Guid.TryParse(identity.FindFirst(Claims.Subject)?.Value, out var userId)
                ? await db.Users.AsNoTracking()
                    .Where(u => u.Id == userId)
                    .Select(u => new
                    {
                        u.IsActive,
                        u.SecurityStamp,
                        Roles = db.UserRoles
                            .Where(ur => ur.UserId == u.Id)
                            .Join(db.Roles, ur => ur.RoleId, r => r.Id, (_, r) => r.Name!)
                            .ToList()
                    })
                    .FirstOrDefaultAsync(context.RequestAborted)
                : null;

            if (account is null || !account.IsActive || !SessionStamp.Matches(context.User, account.SecurityStamp))
            {
                await context.ChallengeAsync(
                    OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
                    new AuthenticationProperties(new Dictionary<string, string?>
                    {
                        [OpenIddictValidationAspNetCoreConstants.Properties.Error] = Errors.InvalidToken,
                        [OpenIddictValidationAspNetCoreConstants.Properties.ErrorDescription] =
                            "This session has ended. Sign in again."
                    }));
                return;
            }

            foreach (var claim in identity.FindAll(Claims.Role).ToList())
            {
                identity.RemoveClaim(claim);
            }

            foreach (var role in account.Roles)
            {
                identity.AddClaim(new Claim(Claims.Role, role));
            }

            await next(context);
        });
}
