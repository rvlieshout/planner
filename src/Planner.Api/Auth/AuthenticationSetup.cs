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
    /// <summary>Carries one consent decision from the web client to /connect/authorize. It says who
    /// approved (or refused) which client a few minutes ago and is never accepted as an API credential.</summary>
    public const string AuthorizeCookieScheme = "Planner.Authorize";

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

        services.AddSingleton<PasskeyCeremonies>();
        services.Configure<IdentityPasskeyOptions>(options =>
        {
            options.ResidentKeyRequirement = "required";
            options.UserVerificationRequirement = "required";
            options.ValidateOrigin = context => ValueTask.FromResult(
                !context.CrossOrigin && PasskeyEndpoints.IsSameOrigin(context.HttpContext, context.Origin));
        });

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
                if (!string.IsNullOrWhiteSpace(auth.Issuer))
                {
                    options.SetIssuer(auth.Issuer);
                }

                options.SetAuthorizationEndpointUris("connect/authorize")
                    .SetTokenEndpointUris("connect/token")
                    .SetUserInfoEndpointUris("connect/userinfo");

                // Passkeys are the primary browser login; password remains for setup/recovery and API tools.
                // Both issue renewable sessions and rebuild authorization claims on refresh. The code
                // flow is for third-party (MCP) clients, which must never see the user's credentials;
                // client permissions keep it off the first-party clients.
                options.AllowAuthorizationCodeFlow()
                    .RequireProofKeyForCodeExchange()
                    .AllowPasswordFlow()
                    .AllowRefreshTokenFlow()
                    .AllowCustomFlow(PasskeyEndpoints.GrantType);

                // "plain" sends the verifier's own value as the challenge, which protects nothing once
                // the authorization request is observed. MCP requires S256; offer nothing weaker.
                options.Configure(server => server.CodeChallengeMethods.Remove(CodeChallengeMethods.Plain));

                options.RegisterScopes(
                    Scopes.OpenId,
                    Scopes.Email,
                    Scopes.Profile,
                    Scopes.Roles,
                    Scopes.OfflineAccess,
                    PlannerScopes.Api,
                    PlannerScopes.Mcp);

                // The only audience a client may ask for. Unregistered resources are rejected by
                // OpenIddict before any handler here runs.
                if (auth.ResolveMcpResource() is { } mcpResource)
                {
                    options.RegisterResources(mcpResource);
                }

                options.SetAccessTokenLifetime(TimeSpan.FromMinutes(auth.AccessTokenMinutes));
                options.SetRefreshTokenLifetime(TimeSpan.FromDays(auth.RefreshTokenDays));

                options.AddSigningCertificate(ServerCertificates.GetOrCreateSigning(auth.KeyDirectory, auth.KeyPassword))
                    .AddEncryptionCertificate(
                        ServerCertificates.GetOrCreateEncryption(auth.KeyDirectory, auth.KeyPassword));

                // Hand out plain JWTs so curl, native clients and any sidecar service can read and
                // validate them without OpenIddict-specific decryption.
                options.DisableAccessTokenEncryption();

                var aspNetCore = options.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough()
                    .EnableUserInfoEndpointPassthrough();

                if (auth.AllowInsecureHttp)
                {
                    aspNetCore.DisableTransportSecurityRequirement();
                }
            })
            .AddValidation(options =>
            {
                // Imports the configured issuer as well as the server's signing keys.
                options.UseLocalServer();
                options.UseAspNetCore();
            });

        services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
            .AddCookie(AuthorizeCookieScheme, options =>
            {
                // Scoped to the authorize endpoint, so no other request ever carries it. Strict is enough:
                // the hop from the consent page to /connect/authorize is a same-origin navigation.
                options.Cookie.Name = "planner.authorize";
                options.Cookie.Path = "/connect/authorize";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.Cookie.IsEssential = true;
                options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
                options.SlidingExpiration = false;

                // Never redirect to a login page this API does not have; the endpoints decide.
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });

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
