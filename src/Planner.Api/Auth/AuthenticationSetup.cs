using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;
using Planner.Api.Authorization;
using Planner.Domain.Identity;
using Planner.Infrastructure;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Planner.Api.Auth;

public static class PlannerPolicies
{
    /// <summary>Owner or admin: manages users, teams and organisation-wide settings.</summary>
    public const string OrgAdmin = "org:admin";

    /// <summary>Owner only: transfers ownership and grants the owner role.</summary>
    public const string OrgOwner = "org:owner";

    /// <summary>Any full user. Excludes guests, who may read and comment but not create content.</summary>
    public const string Contributor = "org:contributor";
}

public static class AuthenticationSetup
{
    public static IServiceCollection AddPlannerAuth(this IServiceCollection services, PlannerAuthOptions auth)
    {
        services.AddIdentityCore<AppUser>(options =>
            {
                options.User.RequireUniqueEmail = true;

                // On-prem installs sit behind the company perimeter; length beats character classes.
                options.Password.RequiredLength = 12;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireDigit = false;

                options.Lockout.MaxFailedAccessAttempts = 10;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddRoles<AppRole>()
            .AddEntityFrameworkStores<PlannerDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();

        // Make Identity emit the claim names OpenIddict and the rest of the API expect.
        services.Configure<IdentityOptions>(options =>
        {
            options.ClaimsIdentity.UserIdClaimType = Claims.Subject;
            options.ClaimsIdentity.UserNameClaimType = Claims.Name;
            options.ClaimsIdentity.RoleClaimType = Claims.Role;
            options.ClaimsIdentity.EmailClaimType = Claims.Email;
        });

        services.AddOpenIddict()
            .AddCore(options => options
                .UseEntityFrameworkCore()
                .UseDbContext<PlannerDbContext>())
            .AddServer(options =>
            {
                options.SetTokenEndpointUris("connect/token")
                    .SetUserInfoEndpointUris("connect/userinfo");

                // Resource-owner password + refresh. A first-party desktop client on an on-prem network
                // has no browser to redirect through; see docs/roles-and-permissions.md for the
                // authorization-code + PKCE upgrade path when one is available.
                options.AllowPasswordFlow()
                    .AllowRefreshTokenFlow();

                options.RegisterScopes(
                    Scopes.OpenId,
                    Scopes.Email,
                    Scopes.Profile,
                    Scopes.Roles,
                    Scopes.OfflineAccess,
                    PlannerScopes.Api);

                options.SetAccessTokenLifetime(TimeSpan.FromMinutes(auth.AccessTokenMinutes));
                options.SetRefreshTokenLifetime(TimeSpan.FromDays(auth.RefreshTokenDays));

                options.AddSigningCertificate(ServerCertificates.GetOrCreateSigning(auth.KeyDirectory, auth.KeyPassword))
                    .AddEncryptionCertificate(
                        ServerCertificates.GetOrCreateEncryption(auth.KeyDirectory, auth.KeyPassword));

                // Hand out plain JWTs so the Avalonia client, curl and any sidecar service can read and
                // validate them without OpenIddict-specific decryption.
                options.DisableAccessTokenEncryption();

                var aspNetCore = options.UseAspNetCore()
                    .EnableTokenEndpointPassthrough()
                    .EnableUserInfoEndpointPassthrough();

                if (auth.AllowInsecureHttp)
                {
                    aspNetCore.DisableTransportSecurityRequirement();
                }
            })
            .AddValidation(options =>
            {
                options.UseLocalServer();
                options.UseAspNetCore();
            });

        services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build())
            .AddPolicy(PlannerPolicies.OrgAdmin, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context => CurrentUser.RoleOf(context.User)
                    is PlannerRoles.Owner or PlannerRoles.Admin))
            .AddPolicy(PlannerPolicies.OrgOwner, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context => CurrentUser.RoleOf(context.User) is PlannerRoles.Owner))
            .AddPolicy(PlannerPolicies.Contributor, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context => CurrentUser.RoleOf(context.User)
                    is PlannerRoles.Owner or PlannerRoles.Admin or PlannerRoles.Member));

        return services;
    }

    /// <summary>Decides which tokens each claim is copied into. Anything not listed here stays on the
    /// server: an access token that carries more than it needs is a liability on the wire.</summary>
    public static IEnumerable<string> GetDestinations(Claim claim)
    {
        switch (claim.Type)
        {
            case Claims.Name or Claims.PreferredUsername:
                yield return Destinations.AccessToken;

                if (claim.Subject?.HasScope(Scopes.Profile) == true)
                {
                    yield return Destinations.IdentityToken;
                }

                yield break;

            case Claims.Email:
                yield return Destinations.AccessToken;

                if (claim.Subject?.HasScope(Scopes.Email) == true)
                {
                    yield return Destinations.IdentityToken;
                }

                yield break;

            case Claims.Role:
                yield return Destinations.AccessToken;

                if (claim.Subject?.HasScope(Scopes.Roles) == true)
                {
                    yield return Destinations.IdentityToken;
                }

                yield break;

            // Never leave the server: Identity's security stamp is an internal invalidation marker.
            case "AspNet.Identity.SecurityStamp":
                yield break;

            default:
                yield return Destinations.AccessToken;
                yield break;
        }
    }
}
