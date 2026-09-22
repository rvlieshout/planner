using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Planner.Domain.Identity;

namespace Planner.Api.Auth;

public static class PasskeyEndpoints
{
    public const string GrantType = "urn:planner:params:oauth:grant-type:passkey";
    public sealed record RegisterPasskeyRequest(string Name, JsonElement Credential);

    public static bool IsSameOrigin(HttpContext context, string? origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
        string.Equals(uri.GetLeftPart(UriPartial.Authority),
            $"{context.Request.Scheme}://{context.Request.Host}", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    public static IEndpointRouteBuilder MapPasskeyEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/connect/passkey/options", async (HttpContext context,
            IPasskeyHandler<AppUser> handler, PasskeyCeremonies ceremonies) =>
        {
            if (!IsSameOrigin(context, context.Request.Headers.Origin)) return Results.BadRequest();
            var options = await handler.MakeRequestOptionsAsync(null, context);
            ceremonies.Begin(context, "login", options.AssertionState);
            return Results.Content(options.RequestOptionsJson, "application/json");
        }).AllowAnonymous().RequireRateLimiting("auth").WithTags("Auth");

        var group = app.MapGroup("/api/v1/me/passkeys").RequireAuthorization()
            .RequireRateLimiting("auth").WithTags("Auth");
        group.MapGet("", async (HttpContext context, UserManager<AppUser> users) =>
        {
            var user = await users.GetUserAsync(context.User);
            if (user is null || !user.IsActive) return Results.Unauthorized();
            context.Response.Headers.CacheControl = "no-store";
            var passkeys = await users.GetPasskeysAsync(user);
            return Results.Ok(passkeys.Select(key => new
            {
                id = WebEncoders.Base64UrlEncode(key.CredentialId), key.Name, key.CreatedAt
            }));
        });
        group.MapPost("/options", async (HttpContext context, UserManager<AppUser> users,
            IPasskeyHandler<AppUser> handler, PasskeyCeremonies ceremonies) =>
        {
            var user = await users.GetUserAsync(context.User);
            if (user is null || !user.IsActive) return Results.Unauthorized();
            if (!IsSameOrigin(context, context.Request.Headers.Origin)) return Results.BadRequest();
            if ((await users.GetPasskeysAsync(user)).Count >= 10)
                return Results.Problem("Remove an unused passkey before adding another.", statusCode: 400);
            var options = await handler.MakeCreationOptionsAsync(new PasskeyUserEntity
            {
                Id = user.Id.ToString(), Name = user.UserName!, DisplayName = user.DisplayName
            }, context);
            ceremonies.Begin(context, "register", options.AttestationState, user.Id.ToString());
            return Results.Content(options.CreationOptionsJson, "application/json");
        });
        group.MapPost("", async (RegisterPasskeyRequest request, HttpContext context,
            UserManager<AppUser> users, IPasskeyHandler<AppUser> handler, PasskeyCeremonies ceremonies) =>
        {
            var user = await users.GetUserAsync(context.User);
            if (user is null || !user.IsActive) return Results.Unauthorized();
            var state = ceremonies.Take(context, "register", user.Id.ToString());
            if (state is null || request.Credential.ValueKind != JsonValueKind.Object ||
                string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 100)
                return Results.Problem("Passkey setup expired or is invalid. Start again.", statusCode: 400);
            if ((await users.GetPasskeysAsync(user)).Count >= 10)
                return Results.Problem("Remove an unused passkey before adding another.", statusCode: 400);
            var result = await handler.PerformAttestationAsync(new PasskeyAttestationContext
            {
                HttpContext = context, CredentialJson = request.Credential.GetRawText(), AttestationState = state
            });
            if (!result.Succeeded || result.UserEntity.Id != user.Id.ToString())
                return Results.Problem("The passkey could not be verified. Start again.", statusCode: 400);
            result.Passkey.Name = request.Name.Trim();
            var saved = await users.AddOrUpdatePasskeyAsync(user, result.Passkey);
            return saved.Succeeded ? Results.NoContent() : Results.Problem("The passkey could not be saved.", statusCode: 400);
        });
        group.MapDelete("/{credentialId}", async (string credentialId, HttpContext context, UserManager<AppUser> users) =>
        {
            var user = await users.GetUserAsync(context.User);
            if (user is null || !user.IsActive) return Results.Unauthorized();
            byte[] id;
            try { id = WebEncoders.Base64UrlDecode(credentialId); }
            catch (FormatException) { return Results.BadRequest(); }
            if (await users.GetPasskeyAsync(user, id) is null) return Results.NotFound();
            if ((await users.GetPasskeysAsync(user)).Count == 1 && !await users.HasPasswordAsync(user))
                return Results.Problem("Add another passkey before removing your last sign-in method.", statusCode: 400);
            var removed = await users.RemovePasskeyAsync(user, id);
            return removed.Succeeded ? Results.NoContent() : Results.Problem("The passkey could not be removed.", statusCode: 400);
        });
        return app;
    }
}
