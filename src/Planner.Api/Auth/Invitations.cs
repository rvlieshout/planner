using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Planner.Domain.Identity;

namespace Planner.Api.Auth;

/// <summary>Bearer invitations use the existing persisted Data Protection key ring and Identity
/// security stamp. There is no separate token table, password, or plaintext secret at rest.</summary>
public sealed class Invitations(UserManager<AppUser> users, IDataProtectionProvider protection)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(72);
    private readonly ITimeLimitedDataProtector protector =
        protection.CreateProtector("Planner.Invitations.v1").ToTimeLimitedDataProtector();

    public (string Token, DateTimeOffset ExpiresAt) Issue(AppUser user)
    {
        if (!user.IsInvitationPending || user.IsActive || string.IsNullOrEmpty(user.SecurityStamp))
            throw new InvalidOperationException("Only inactive, pending accounts can receive invitations.");

        var expiresAt = DateTimeOffset.UtcNow.Add(Lifetime);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Ticket(user.Id, user.SecurityStamp));
        return (WebEncoders.Base64UrlEncode(protector.Protect(payload, expiresAt)), expiresAt);
    }

    public async Task<(AppUser User, DateTimeOffset ExpiresAt)?> InspectAsync(Guid userId, string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096)
            return null;

        Ticket? ticket;
        DateTimeOffset expiresAt;
        try
        {
            ticket = JsonSerializer.Deserialize<Ticket>(
                protector.Unprotect(WebEncoders.Base64UrlDecode(token), out expiresAt));
        }
        catch (Exception error) when (error is CryptographicException or FormatException or JsonException)
        {
            return null;
        }

        if (ticket is null || ticket.UserId != userId)
            return null;

        var user = await users.FindByIdAsync(userId.ToString());
        return user is { IsInvitationPending: true, IsActive: false } &&
               !string.IsNullOrEmpty(user.SecurityStamp) && user.SecurityStamp == ticket.SecurityStamp
            ? (user, expiresAt)
            : null;
    }

    public async Task<IdentityResult> AcceptAsync(AppUser user, string password)
    {
        if (!user.IsInvitationPending || user.IsActive)
            return IdentityResult.Failed(new IdentityError { Code = "InvalidInvitation", Description = "Invalid invitation." });

        // AddPasswordAsync validates the password and writes its hash, the new security stamp, and
        // these flags in ONE Identity user update. EF's concurrency stamp makes simultaneous
        // acceptance/renewal fail rather than overwrite a password or consume the invitation twice.
        user.IsActive = true;
        user.EmailConfirmed = true;
        var result = await users.AddPasswordAsync(user, password);
        if (!result.Succeeded)
        {
            user.IsActive = false;
            user.EmailConfirmed = false;
        }
        return result;
    }

    private sealed record Ticket(Guid UserId, string SecurityStamp);
}
