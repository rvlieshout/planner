using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Server;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using Planner.Api.Auth;
using Planner.Infrastructure;

namespace Planner.Api.Mcp;

/// <summary>The MCP endpoint: a read-only view of Planner for AI assistants, acting as the signed-in
/// user and never as more. See docs/mcp.md.</summary>
public static class McpSetup
{
    public const string Path = "/mcp";
    public const string Policy = "mcp";

    private const string Instructions =
        """
        Planner is a project tracker: teams own projects, projects hold milestones and issues, and every
        issue sits in a workflow state of type backlog, unstarted, started, completed or canceled.
        Issues are referred to by key, e.g. DEV-42. Teams by key or name.

        Everything here is read with the signed-in user's permissions, so a team or issue that cannot be
        found may simply be one they cannot see.

        For questions about what happened over a period ("what happened in the development team last
        week"), call team_digest first: it returns what was completed, created, started and canceled, the
        work in progress, and project progress for that window in one call. Drill into single issues with
        get_issue only where the digest is not enough. Times are in the user's time zone.
        """;

    public static IServiceCollection AddPlannerMcp(this IServiceCollection services, PlannerAuthOptions auth)
    {
        services.AddScoped<McpReader>();
        services.AddScoped<IActiveAccounts, ActiveAccounts>();

        services.AddMcpServer(options =>
            {
                options.ServerInfo = new() { Name = "planner", Title = "Planner", Version = "1.0.0" };
                options.ServerInstructions = Instructions;
            })
            // Stateless: every call is an ordinary request carrying its own bearer token, so the tools
            // run with that request's user and nothing about a caller outlives the call.
            .WithHttpTransport(options => options.Stateless = true)
            .WithTools<WorkspaceTools>()
            .WithTools<IssueTools>()
            .WithTools<FeedTools>()
            .WithTools<DigestTools>()
            .WithPrompts<PlannerPrompts>();

        var resource = auth.ResolveMcpResource();

        services.AddAuthentication()
            .AddMcp(options =>
            {
                // This scheme only issues challenges and serves the protected-resource metadata; tokens
                // are validated by OpenIddict like everywhere else in the API.
                options.ForwardAuthenticate = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
                options.ForwardForbid = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;

                options.ResourceMetadata = new()
                {
                    ResourceName = "Planner",
                    ScopesSupported = [PlannerScopes.Mcp, OpenIddictConstants.Scopes.OfflineAccess]
                };

                if (resource is not null)
                {
                    options.ResourceMetadata.Resource = resource.AbsoluteUri;
                }

                // Planner is its own authorization server. Named from the issuer when there is one, and
                // from the request otherwise, which is what OpenIddict does for local installs too.
                options.Events.OnResourceMetadataRequest = context =>
                {
                    var request = context.HttpContext.Request;
                    var issuer = auth.Issuer is { Length: > 0 } configured
                        ? configured
                        : $"{request.Scheme}://{request.Host}{request.PathBase}/";

                    context.ResourceMetadata!.AuthorizationServers = [issuer];
                    context.ResourceMetadata.Resource ??= $"{request.Scheme}://{request.Host}{request.PathBase}{Path}";
                    return Task.CompletedTask;
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(Policy, policy => policy
                .AddAuthenticationSchemes(McpAuthenticationDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => resource is not null && context.User.HasAudience(resource.AbsoluteUri))
                // An assistant may keep calling for as long as its access token lives. Deactivating the
                // account should stop it now, not when that token next needs refreshing.
                .RequireAssertion(async context =>
                {
                    if (context.Resource is not HttpContext http ||
                        !Guid.TryParse(context.User.GetClaim(OpenIddictConstants.Claims.Subject), out var userId))
                    {
                        return false;
                    }

                    return await http.RequestServices.GetRequiredService<IActiveAccounts>()
                        .IsActiveAsync(userId, http.RequestAborted);
                }));

        return services;
    }

    public static IEndpointRouteBuilder MapPlannerMcp(this IEndpointRouteBuilder app)
    {
        app.MapMcp(Path)
            .RequireAuthorization(Policy)
            .RequireRateLimiting("mcp")
            .ExcludeFromDescription();

        return app;
    }

    /// <summary>Keeps MCP tokens on the MCP endpoint.
    ///
    /// A token minted for an assistant says so in its audience. The REST API and the realtime hub accept
    /// any valid token, so without this an assistant's token would carry every write the user can make,
    /// when the user approved a read-only tool. First-party tokens carry no audience and pass untouched.</summary>
    public static IApplicationBuilder UseMcpAudienceBoundary(this IApplicationBuilder app, PlannerAuthOptions auth)
    {
        var resource = auth.ResolveMcpResource()?.AbsoluteUri;

        return app.Use(async (context, next) =>
        {
            if (resource is not null &&
                context.User.Identity?.IsAuthenticated == true &&
                context.User.HasAudience(resource) &&
                !context.Request.Path.StartsWithSegments(Path))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    title = "This token is for the MCP endpoint only.",
                    status = StatusCodes.Status403Forbidden
                });
                return;
            }

            await next(context);
        });
    }
}

/// <summary>Whether an account may still act. A seam of its own so the endpoint can be exercised
/// without a database.</summary>
public interface IActiveAccounts
{
    Task<bool> IsActiveAsync(Guid userId, CancellationToken ct);
}

public sealed class ActiveAccounts(PlannerDbContext db) : IActiveAccounts
{
    public Task<bool> IsActiveAsync(Guid userId, CancellationToken ct) =>
        db.Users.AnyAsync(u => u.Id == userId && u.IsActive, ct);
}
