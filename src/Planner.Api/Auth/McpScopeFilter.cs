using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace Planner.Api.Auth;

/// <summary>Grants an MCP client the scopes it may have out of those it asked for, instead of refusing
/// the whole request.
///
/// MCP clients do not know which scopes matter here, so many (ChatGPT, for one) ask for every scope the
/// discovery document lists: openid, email, profile, roles, planner.api, planner.mcp. OpenIddict refuses
/// any request naming a scope the client is not permitted, and planner.api is for the first-party
/// clients, so those sign-ins failed outright. OAuth lets a server grant fewer scopes than requested
/// (RFC 6749, 3.3); the token response says which were granted. The client ends up with exactly what
/// its permissions allow, as it would have with a narrower request.
///
/// Only clients permitted the authorization code flow are filtered, which is the MCP clients. The
/// first-party clients keep OpenIddict's strict check.</summary>
public sealed class McpScopeFilter(IOpenIddictApplicationManager applications) :
    IOpenIddictServerHandler<ValidateAuthorizationRequestContext>,
    IOpenIddictServerHandler<ValidateTokenRequestContext>
{
    /// <summary>Granted with no scope permission: OpenIddict special-cases these two.</summary>
    private static readonly HashSet<string> Unrestricted = [Scopes.OpenId, Scopes.OfflineAccess];

    public static OpenIddictServerHandlerDescriptor AuthorizationDescriptor { get; } =
        OpenIddictServerHandlerDescriptor.CreateBuilder<ValidateAuthorizationRequestContext>()
            .UseScopedHandler<McpScopeFilter>()
            // Just before OpenIddict checks the requested scopes against the client's permissions.
            .SetOrder(OpenIddictServerHandlers.Authentication.ValidateScopePermissions.Descriptor.Order - 1)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    public static OpenIddictServerHandlerDescriptor TokenDescriptor { get; } =
        OpenIddictServerHandlerDescriptor.CreateBuilder<ValidateTokenRequestContext>()
            .UseScopedHandler<McpScopeFilter>()
            // Before OpenIddict rejects a scope parameter on a code exchange, and before the permissions.
            .SetOrder(Math.Min(
                OpenIddictServerHandlers.Exchange.ValidateScopeParameter.Descriptor.Order,
                OpenIddictServerHandlers.Exchange.ValidateScopePermissions.Descriptor.Order) - 1)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    public ValueTask HandleAsync(ValidateAuthorizationRequestContext context) =>
        FilterAsync(context.Request, context.ClientId, context.CancellationToken);

    public async ValueTask HandleAsync(ValidateTokenRequestContext context)
    {
        // A code carries the scopes granted when it was issued, so a scope on its exchange means nothing.
        // OpenIddict refuses one outright; some clients send it anyway (their authorization request's
        // list, again), so for MCP clients it is dropped rather than failing the sign-in at its last step.
        if (context.Request.IsAuthorizationCodeGrantType())
        {
            if (!string.IsNullOrEmpty(context.Request.Scope) &&
                await McpClientAsync(context.ClientId, context.CancellationToken) is not null)
            {
                context.Request.Scope = null;
            }

            return;
        }

        await FilterAsync(context.Request, context.ClientId, context.CancellationToken);
    }

    /// <summary>The client, if it is one this filter is for: permitted the authorization code flow.</summary>
    private async ValueTask<object?> McpClientAsync(string? clientId, CancellationToken ct) =>
        !string.IsNullOrEmpty(clientId) &&
        await applications.FindByClientIdAsync(clientId, ct) is { } client &&
        await applications.HasPermissionAsync(client, Permissions.GrantTypes.AuthorizationCode, ct)
            ? client
            : null;

    private async ValueTask FilterAsync(OpenIddictRequest request, string? clientId, CancellationToken ct)
    {
        var requested = request.GetScopes();

        if (requested.IsDefaultOrEmpty || await McpClientAsync(clientId, ct) is not { } client)
        {
            return;
        }

        var granted = new List<string>(requested.Length);
        foreach (var scope in requested)
        {
            if (Unrestricted.Contains(scope) ||
                await applications.HasPermissionAsync(client, Permissions.Prefixes.Scope + scope, ct))
            {
                granted.Add(scope);
            }
        }

        if (granted.Count != requested.Length)
        {
            request.Scope = string.Join(' ', granted);
        }
    }
}
