using System.Text.Json;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Planner.Api.Auth;

/// <summary>What an MCP client may do, whether it was configured (planner-mcp) or registered itself.
///
/// One definition for both, so a client that registered dynamically can never be given more than the
/// configured one: the code flow with PKCE, refresh tokens, the MCP scope and the MCP resource. It never
/// holds a secret and never gets a direct grant such as password.</summary>
public static class McpClients
{
    /// <summary>Marks a client that registered itself. Its name is its own claim, not something this
    /// installation vouches for, and the consent screen says so.</summary>
    public const string DynamicProperty = "planner:dynamic";

    public static OpenIddictApplicationDescriptor Describe(
        string clientId,
        string displayName,
        IEnumerable<Uri> redirectUris,
        Uri? resource,
        bool dynamic)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            DisplayName = displayName,
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

        foreach (var uri in redirectUris)
        {
            descriptor.RedirectUris.Add(uri);
        }

        if (resource is not null)
        {
            descriptor.AddResourcePermissions(resource.AbsoluteUri);
        }

        if (dynamic)
        {
            descriptor.Properties[DynamicProperty] = JsonSerializer.SerializeToElement(true);
        }

        return descriptor;
    }

    public static async Task<bool> IsDynamicAsync(
        IOpenIddictApplicationManager applications, object client, CancellationToken ct)
    {
        var properties = await applications.GetPropertiesAsync(client, ct);
        return properties.TryGetValue(DynamicProperty, out var value) && value.ValueKind == JsonValueKind.True;
    }
}
