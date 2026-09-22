using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;

namespace Planner.Api.Auth;

/// <summary>Short-lived, single-use WebAuthn state. Only an opaque browser binding leaves the server.
/// Restarting the API cancels pending ceremonies; registered passkeys live in Postgres.</summary>
public sealed class PasskeyCeremonies : IDisposable
{
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 10_000 });
    private readonly object gate = new();
    private sealed record Ceremony(string State, string? UserId);
    private static string CookieName(HttpContext context, string operation) => $"{(context.Request.IsHttps ? "__Host-" : "")}planner.passkey.{operation}";
    private static CookieOptions Cookie(HttpContext context) => new()
    {
        HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Strict,
        Path = "/", MaxAge = TimeSpan.FromMinutes(5), IsEssential = true
    };

    public void Begin(HttpContext context, string operation, string? state, string? userId = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var id = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        lock (gate)
        {
            if (context.Request.Cookies.TryGetValue(CookieName(context, operation), out var previous))
                cache.Remove(operation + previous);
            cache.Set(operation + id, new Ceremony(state, userId), new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5), Size = 1
            });
        }
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Cookies.Append(CookieName(context, operation), id, Cookie(context));
    }

    public string? Take(HttpContext context, string operation, string? userId = null)
    {
        if (!context.Request.Cookies.TryGetValue(CookieName(context, operation), out var id)) return null;
        context.Response.Cookies.Delete(CookieName(context, operation), Cookie(context));
        lock (gate)
        {
            var key = operation + id;
            if (!cache.TryGetValue<Ceremony>(key, out var ceremony)) return null;
            cache.Remove(key);
            return ceremony?.UserId == userId ? ceremony?.State : null;
        }
    }

    public void Dispose() => cache.Dispose();
}
