using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Planner.Api.Auth;
using Planner.Api.Authorization;
using Planner.Api.Common;
using Planner.Api.Realtime;
using Planner.Contracts.Auth;
using Planner.Contracts.Common;
using Planner.Contracts.Realtime;
using Planner.Domain.Identity;
using Planner.Infrastructure;

namespace Planner.Api.Endpoints;

public static class InvitationEndpoints
{
    public static IEndpointRouteBuilder MapInvitationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/invitations").WithTags("Invitations");
        // Tokens and the identity revealed by a valid token must not be cached by intermediaries.
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        group.MapPost("/", CreateAsync).RequireAuthorization(PlannerPolicies.OrgAdmin)
            .WithSummary("Invite a new user; manually share the returned token");
        group.MapPost("/{id:b58}/renew", RenewAsync).RequireAuthorization(PlannerPolicies.OrgAdmin)
            .WithSummary("Replace a pending invitation, invalidating its previous link");
        group.MapPost("/inspect", InspectAsync).AllowAnonymous().RequireRateLimiting("auth")
            .WithSummary("Preview a valid invitation without consuming it");
        group.MapPost("/accept", AcceptAsync).AllowAnonymous().RequireRateLimiting("auth")
            .WithSummary("Accept an invitation and choose the account's first password");
        return app;
    }

    private static async Task<IResult> CreateAsync(
        CreateInvitationRequest request, UserManager<AppUser> users, PlannerDbContext db,
        CurrentUser current, Invitations invitations, IRealtimeNotifier notifier, CancellationToken ct)
    {
        var validation = new Validation()
            .Required(request.Email, "email")
            .Required(request.DisplayName, "displayName")
            .MaxLength(request.DisplayName, 150, "displayName")
            .Required(request.TimeZone, "timeZone")
            .MaxLength(request.TimeZone, 60, "timeZone");
        if (request.Role is null || !PlannerRoles.All.ContainsKey(request.Role))
            validation.Add("role", $"Unknown role. Valid roles: {string.Join(", ", PlannerRoles.All.Keys)}.");
        if (request.Role == PlannerRoles.Owner && !current.IsOwner)
            return ApiResults.Forbidden("Only the owner can grant the owner role.");
        if (validation.HasErrors)
            return validation.ToResult();

        var userId = Guid.CreateVersion7();
        AppUser? created = null;
        // Persistence enables transient retries. Retry the entire transaction with fresh tracked
        // entities; a stable ID lets a retry recognize an already committed creation.
        var outcome = await db.Database.CreateExecutionStrategy().ExecuteAsync<IResult>(async () =>
        {
            db.ChangeTracker.Clear();
            var user = await users.FindByIdAsync(userId.ToString());
            if (user is null)
            {
                user = new AppUser
                {
                    Id = userId,
                    UserName = request.Email.Trim(),
                    Email = request.Email.Trim(),
                    DisplayName = request.DisplayName.Trim(),
                    TimeZone = request.TimeZone,
                    IsActive = false,
                    EmailConfirmed = false,
                    InvitationExpiresAt = DateTimeOffset.UtcNow.Add(Invitations.Lifetime)
                };
                // Do not leave an orphaned account if role assignment fails.
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var result = await users.CreateAsync(user);
                if (!result.Succeeded)
                    return IdentityProblem(result);
                result = await users.AddToRoleAsync(user, request.Role!);
                if (!result.Succeeded)
                    return IdentityProblem(result);
                await transaction.CommitAsync(ct);
            }
            created = user;
            return Results.Created($"/api/v1/users/{user.Id.ToBase58()}", Response(user, request.Role!, invitations));
        });
        if (created is not null)
            await notifier.UserChanged(ChangeKind.Created, Mapping.ToUserSummary(created));
        return outcome;
    }

    private static async Task<IResult> RenewAsync(
        Guid id, UserManager<AppUser> users, CurrentUser current, Invitations invitations)
    {
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null)
            return ApiResults.NotFound("That user");
        if (!user.IsInvitationPending || user.IsActive)
            return ApiResults.BadRequest("Only pending invitations can be renewed.");
        var roles = await users.GetRolesAsync(user);
        if (roles.Contains(PlannerRoles.Owner) && !current.IsOwner)
            return ApiResults.Forbidden("Only the owner can renew an owner invitation.");

        // Saved by the same user update that rotates the stamp.
        user.InvitationExpiresAt = DateTimeOffset.UtcNow.Add(Invitations.Lifetime);
        var result = await users.UpdateSecurityStampAsync(user);
        return result.Succeeded
            ? Results.Ok(Response(user, roles.FirstOrDefault() ?? PlannerRoles.Guest, invitations))
            : IdentityProblem(result);
    }

    private static async Task<IResult> InspectAsync(InspectInvitationRequest request, Invitations invitations)
    {
        var invitation = await invitations.InspectAsync(request.UserId, request.Token);
        return invitation is { } valid
            ? Results.Ok(new InvitationPreview(valid.User.Email!, valid.User.DisplayName, valid.ExpiresAt))
            : InvalidInvitation();
    }

    private static async Task<IResult> AcceptAsync(
        AcceptInvitationRequest request, Invitations invitations, IRealtimeNotifier notifier)
    {
        var invitation = await invitations.InspectAsync(request.UserId, request.Token);
        if (invitation is not { } valid)
            return InvalidInvitation();
        if (string.IsNullOrWhiteSpace(request.Password))
            return new Validation().Required(request.Password, "password").ToResult();
        var result = await invitations.AcceptAsync(valid.User, request.Password);
        if (!result.Succeeded)
            return result.Errors.Any(error => error.Code == "ConcurrencyFailure")
                ? InvalidInvitation()
                : IdentityProblem(result);

        await notifier.UserChanged(ChangeKind.Updated, Mapping.ToUserSummary(valid.User));
        return Results.NoContent();
    }

    private static InvitationResponse Response(AppUser user, string role, Invitations invitations)
    {
        var (token, expiresAt) = invitations.Issue(user);
        var detail = new UserDetail(user.Id, user.Email!, user.DisplayName, user.AvatarUrl,
            user.TimeZone, role, user.IsActive, user.CreatedAt, user.LastSeenAt, user.IsInvitationPending,
            user.InvitationExpiresAt);
        return new InvitationResponse(detail, token, expiresAt);
    }

    private static IResult InvalidInvitation() =>
        ApiResults.BadRequest("This invitation is invalid, expired, or already used. Ask an administrator for a new link.");

    private static IResult IdentityProblem(IdentityResult result) =>
        Results.ValidationProblem(result.Errors.GroupBy(error => error.Code)
            .ToDictionary(group => group.Key, group => group.Select(error => error.Description).ToArray()));
}
