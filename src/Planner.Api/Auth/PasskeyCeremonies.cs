using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;

namespace Planner.Api.Auth;

/// <summary>Short-lived, single-use WebAuthn state. Only an opaque browser binding leaves the server.
/// Restarting the API cancels pending ceremonies; registered passkeys live in Postgres.
///
/// Signing in and adding a passkey are kept apart. Anyone can start a sign-in, so that store can be
/// flooded from outside; adding a passkey needs an account, and must not be crowded out by people who
/// have none. Either store, when full, makes room for the newest ceremony rather than refusing it:
/// someone at the sign-in page now matters more than a ceremony begun minutes ago and likely abandoned.</summary>
public sealed class PasskeyCeremonies : IDisposable
{
    /// <summary>A ceremony is a few hundred bytes, so this is tens of megabytes at the very most.</summary>
    internal const int MaxSignIns = 100_000;

    internal const int MaxRegistrations = 10_000;

    private readonly MemoryCache signIns;
    private readonly MemoryCache registrations;
    private readonly object gate = new();

    public PasskeyCeremonies() : this(MaxSignIns, MaxRegistrations)
    {
    }

    public PasskeyCeremonies(int maxSignIns, int maxRegistrations)
    {
        signIns = new MemoryCache(new MemoryCacheOptions { SizeLimit = maxSignIns });
        registrations = new MemoryCache(new MemoryCacheOptions { SizeLimit = maxRegistrations });
    }

    private sealed record Ceremony(string State, string? UserId);
    private static string CookieName(HttpContext context, string operation) => $"{(context.Request.IsHttps ? "__Host-" : "")}planner.passkey.{operation}";
    private static CookieOptions Cookie(HttpContext context) => new()
    {
        HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Strict,
        Path = "/", MaxAge = TimeSpan.FromMinutes(5), IsEssential = true
    };

    private MemoryCache StoreFor(string operation) => operation == "login" ? signIns : registrations;

    public void Begin(HttpContext context, string operation, string? state, string? userId = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var id = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var cache = StoreFor(operation);
        lock (gate)
        {
            if (context.Request.Cookies.TryGetValue(CookieName(context, operation), out var previous))
                cache.Remove(operation + previous);

            var key = operation + id;
            var ceremony = new Ceremony(state, userId);
            var options = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5), Size = 1
            };

            cache.Set(key, ceremony, options);

            // A full cache drops the new entry instead of an old one, and tidies up later on its own
            // schedule. Make the room now, so this ceremony is the one that survives.
            if (!cache.TryGetValue(key, out _))
            {
                cache.Compact(0.25);
                cache.Set(key, ceremony, options);
            }
        }
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Cookies.Append(CookieName(context, operation), id, Cookie(context));
    }

    public string? Take(HttpContext context, string operation, string? userId = null)
    {
        if (!context.Request.Cookies.TryGetValue(CookieName(context, operation), out var id)) return null;
        context.Response.Cookies.Delete(CookieName(context, operation), Cookie(context));
        var cache = StoreFor(operation);
        lock (gate)
        {
            var key = operation + id;
            if (!cache.TryGetValue<Ceremony>(key, out var ceremony)) return null;
            cache.Remove(key);
            return ceremony?.UserId == userId ? ceremony?.State : null;
        }
    }

    public void Dispose()
    {
        signIns.Dispose();
        registrations.Dispose();
    }
}
