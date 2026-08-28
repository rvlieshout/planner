using System.Security.Claims;
using OpenIddict.Abstractions;
using Planner.Domain.Identity;

namespace Planner.Api.Authorization;

/// <summary>Typed view over the calling principal, so endpoints never poke at raw claim strings.</summary>
public sealed class CurrentUser(IHttpContextAccessor accessor)
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public Guid Id
    {
        get
        {
            var subject = Principal?.FindFirstValue(OpenIddictConstants.Claims.Subject)
                          ?? Principal?.FindFirstValue(ClaimTypes.NameIdentifier);

            return Guid.TryParse(subject, out var id)
                ? id
                : throw new InvalidOperationException("The access token carries no usable subject claim.");
        }
    }

    public string? Email =>
        Principal?.FindFirstValue(OpenIddictConstants.Claims.Email) ?? Principal?.FindFirstValue(ClaimTypes.Email);

    /// <summary>The organisation role. Exactly one is assigned per user.</summary>
    public string Role =>
        Principal?.FindFirstValue(OpenIddictConstants.Claims.Role)
        ?? Principal?.FindFirstValue(ClaimTypes.Role)
        ?? PlannerRoles.Guest;

    public bool IsOwner => Role == PlannerRoles.Owner;

    /// <summary>Owners are administrators too; every admin check should go through this.</summary>
    public bool IsAdmin => Role is PlannerRoles.Owner or PlannerRoles.Admin;

    public bool IsGuest => Role == PlannerRoles.Guest;

    /// <summary>Reads the organisation role straight off a principal. Authorization policies run before
    /// any scoped service is resolved, so they use this rather than an injected CurrentUser.</summary>
    public static string RoleOf(ClaimsPrincipal principal) =>
        principal.FindFirstValue(OpenIddictConstants.Claims.Role)
        ?? principal.FindFirstValue(ClaimTypes.Role)
        ?? PlannerRoles.Guest;
}
