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
        await EnsureScopeAsync(ct);
        await EnsurePublicClientAsync(auth.DesktopClientId, "Planner desktop client", ct);
        await EnsurePublicClientAsync(auth.WebClientId, "Planner web client", ct);
    }

    private async Task EnsureScopeAsync(CancellationToken ct)
    {
        if (await scopes.FindByNameAsync(PlannerScopes.Api, ct) is not null)
        {
            return;
        }

        await scopes.CreateAsync(new OpenIddictScopeDescriptor
        {
            Name = PlannerScopes.Api,
            DisplayName = "Planner API",
            Description = "Full access to teams, projects, milestones, issues and documents."
        }, ct);

        logger.LogInformation("Registered OAuth scope {Scope}", PlannerScopes.Api);
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

        var existing = await applications.FindByClientIdAsync(clientId, ct);

        if (existing is null)
        {
            await applications.CreateAsync(descriptor, ct);
            logger.LogInformation("Registered OAuth client {ClientId}", clientId);
            return;
        }

        await applications.UpdateAsync(existing, descriptor, ct);
    }
}
