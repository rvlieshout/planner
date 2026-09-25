using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Planner.Api.Auth;

/// <summary>Registers the OAuth clients this installation accepts. Runs on every start and is
/// idempotent: an existing client is updated in place so permission changes ship with the API.</summary>
public sealed class OpenIddictClientSeeder(
    IOpenIddictApplicationManager applications,
    IOpenIddictScopeManager scopes,
    ILogger<OpenIddictClientSeeder> logger)
{
    public async Task SeedAsync(PlannerAuthOptions auth, CancellationToken ct = default)
    {
        await EnsureScopeAsync(PlannerScopes.Api, "Planner API",
            "Full access to teams, projects, milestones, issues and documents.", ct);
        await EnsureScopeAsync(PlannerScopes.Mcp, "Planner for AI assistants",
            "Act as the signed-in user through the MCP endpoint, within that user's permissions.", ct);
        await EnsurePublicClientAsync(auth.DesktopClientId, "Planner desktop client", ct);
        await EnsurePublicClientAsync(auth.WebClientId, "Planner web client", ct);
        await EnsureMcpClientAsync(auth, ct);
    }

    private async Task EnsureScopeAsync(string name, string displayName, string description, CancellationToken ct)
    {
        if (await scopes.FindByNameAsync(name, ct) is not null)
        {
            return;
        }

        await scopes.CreateAsync(new OpenIddictScopeDescriptor
        {
            Name = name,
            DisplayName = displayName,
            Description = description
        }, ct);

        logger.LogInformation("Registered OAuth scope {Scope}", name);
    }

    /// <summary>The client AI assistants connect through. Unlike the first-party clients it never sees a
    /// password or passkey: the user signs in on this installation's own page and the client receives a
    /// code, which only the holder of the PKCE verifier can redeem.</summary>
    private async Task EnsureMcpClientAsync(PlannerAuthOptions auth, CancellationToken ct)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = auth.McpClientId,
            DisplayName = "AI assistant (MCP)",
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Explicit,
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
                Permissions.Scopes.Email,
                Permissions.Scopes.Profile,
                Permissions.Prefixes.Scope + PlannerScopes.Mcp
            },
            Requirements =
            {
                Requirements.Features.ProofKeyForCodeExchange
            }
        };

        foreach (var uri in auth.McpRedirectUris)
        {
            descriptor.RedirectUris.Add(new Uri(uri));
        }

        if (auth.ResolveMcpResource() is { } resource)
        {
            descriptor.AddResourcePermissions(resource.AbsoluteUri);
        }
        else
        {
            logger.LogWarning(
                "Neither Planner:Auth:McpResource nor Planner:Auth:Issuer is set, so MCP clients cannot sign in");
        }

        await UpsertAsync(descriptor, ct);
    }

    private async Task EnsurePublicClientAsync(string clientId, string displayName, CancellationToken ct)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            DisplayName = displayName,

            // Public: a desktop binary that ships to every workstation cannot hold a secret, and a
            // browser application is source anyone can read. So the client id is an identifier, not a
            // credential. The user's password is the credential.
            ClientType = ClientTypes.Public,
            Permissions =
            {
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.Password,
                Permissions.GrantTypes.RefreshToken,
                Permissions.Prefixes.GrantType + PasskeyEndpoints.GrantType,
                Permissions.Scopes.Email,
                Permissions.Scopes.Profile,
                Permissions.Scopes.Roles,
                Permissions.Prefixes.Scope + PlannerScopes.Api
            }
        };

        await UpsertAsync(descriptor, ct);
    }

    private async Task UpsertAsync(OpenIddictApplicationDescriptor descriptor, CancellationToken ct)
    {
        var existing = await applications.FindByClientIdAsync(descriptor.ClientId!, ct);

        if (existing is null)
        {
            await applications.CreateAsync(descriptor, ct);
            logger.LogInformation("Registered OAuth client {ClientId}", descriptor.ClientId);
            return;
        }

        await applications.UpdateAsync(existing, descriptor, ct);
    }
}
