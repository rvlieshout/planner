using Microsoft.Net.Http.Headers;

namespace Planner.Api.Common;

/// <summary>Browsers cannot set an Authorization header on a WebSocket handshake, so SignalR clients
/// running in one pass the token as a query parameter instead. This lifts it into the header before
/// authentication runs, which keeps the token validation path identical for every transport.</summary>
public sealed class SignalRAuthenticationMiddleware(RequestDelegate next, string hubPath)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments(hubPath) &&
            !context.Request.Headers.ContainsKey(HeaderNames.Authorization) &&
            context.Request.Query.TryGetValue("access_token", out var token) &&
            !string.IsNullOrEmpty(token))
        {
            context.Request.Headers.Authorization = $"Bearer {token}";
        }

        await next(context);
    }
}
