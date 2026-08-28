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

public static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/v1/me").WithTags("Me");

        me.MapGet("/", GetMeAsync)
            .WithSummary("The caller's profile, organisation role and team memberships");

        me.MapPatch("/", UpdateMeAsync)
            .WithSummary("Update the caller's own profile");

        me.MapPost("/password", ChangeOwnPasswordAsync)
            .WithSummary("Change the caller's own password");

        var users = app.MapGroup("/api/v1/users").WithTags("Users");

        users.MapGet("/", ListAsync)
            .WithSummary("Directory of users, for assignee and lead pickers");

        users.MapGet("/{id:guid}", GetAsync)
            .WithSummary("A single user");

        users.MapPost("/", CreateAsync)
            .RequireAuthorization(PlannerPolicies.OrgAdmin)
            .WithSummary("Create a user account");

        users.MapPatch("/{id:guid}", UpdateAsync)
            .RequireAuthorization(PlannerPolicies.OrgAdmin)
            .WithSummary("Update a user's profile, role or active state");

        users.MapPost("/{id:guid}/password", ResetPasswordAsync)
            .RequireAuthorization(PlannerPolicies.OrgAdmin)
            .WithSummary("Set a user's password without knowing the current one");

        users.MapDelete("/{id:guid}", DeactivateAsync)
            .RequireAuthorization(PlannerPolicies.OrgAdmin)
            .WithSummary("Deactivate a user; authored content is kept");

        return app;
    }

    private static async Task<IResult> GetMeAsync(
        PlannerDbContext db,
        UserManager<AppUser> userManager,
        CurrentUser current,
        CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == current.Id, ct);
        if (user is null)
        {
            return ApiResults.NotFound("Your account");
        }

        var teams = await db.TeamMembers
            .Where(m => m.UserId == current.Id)
            .OrderBy(m => m.Team.Key)
            .Select(m => new MeTeamMembership(m.TeamId, m.Team.Key, m.Team.Name, m.Role.ToString()))
            .ToListAsync(ct);

        var roles = await userManager.GetRolesAsync(user);

        return Results.Ok(new MeResponse(
            user.Id,
            user.Email!,
            user.DisplayName,
            user.AvatarUrl,
            user.TimeZone,
            roles.FirstOrDefault() ?? PlannerRoles.Guest,
            user.IsActive,
            teams));
    }

    private static async Task<IResult> UpdateMeAsync(
        UpdateProfileRequest request,
        UserManager<AppUser> userManager,
        CurrentUser current,
        IRealtimeNotifier notifier,
        CancellationToken ct)
    {
        var user = await userManager.FindByIdAsync(current.Id.ToString());
        if (user is null)
        {
            return ApiResults.NotFound("Your account");
        }

        var validation = new Validation();
        if (request.DisplayName.TryGet(out var displayName))
        {
            validation.Required(displayName, "displayName").MaxLength(displayName, 150, "displayName");
        }

        if (validation.HasErrors)
        {
            return validation.ToResult();
        }

        user.DisplayName = request.DisplayName.Or(user.DisplayName)!;
        user.AvatarUrl = request.AvatarUrl.Or(user.AvatarUrl);
        user.TimeZone = request.TimeZone.Or(user.TimeZone)!;

        var result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            return IdentityProblem(result);
        }

        var summary = Mapping.ToUserSummary(user);
        await notifier.UserChanged(ChangeKind.Updated, summary);
        return Results.Ok(summary);
    }

    private static async Task<IResult> ChangeOwnPasswordAsync(
        ChangePasswordRequest request,
        UserManager<AppUser> userManager,
        CurrentUser current)
    {
        var user = await userManager.FindByIdAsync(current.Id.ToString());
        if (user is null)
        {
            return ApiResults.NotFound("Your account");
        }

        var result = await userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        return result.Succeeded ? Results.NoContent() : IdentityProblem(result);
    }

    private static async Task<IResult> ListAsync(
        PlannerDbContext db,
        [AsParameters] PageQuery paging,
        string? search,
        bool? includeInactive,
        CancellationToken ct)
    {
        var query = db.Users.AsNoTracking().AsQueryable();

        if (includeInactive != true)
        {
            query = query.Where(u => u.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(u =>
                EF.Functions.ILike(u.DisplayName, pattern) || EF.Functions.ILike(u.Email!, pattern));
        }

        var total = await query.LongCountAsync(ct);
        var items = await query
            .OrderBy(u => u.DisplayName)
            .Skip(paging.Skip)
            .Take(paging.NormalizedSize)
            .Select(Mapping.UserSummaryProjection)
            .ToListAsync(ct);

        return Results.Ok(new PagedResult<UserSummary>(items, paging.NormalizedPage, paging.NormalizedSize, total));
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        PlannerDbContext db,
        UserManager<AppUser> userManager,
        CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return ApiResults.NotFound("That user");
        }

        var roles = await userManager.GetRolesAsync(user);

        return Results.Ok(new UserDetail(
            user.Id,
            user.Email!,
            user.DisplayName,
            user.AvatarUrl,
            user.TimeZone,
            roles.FirstOrDefault() ?? PlannerRoles.Guest,
            user.IsActive,
            user.CreatedAt,
            user.LastSeenAt));
    }

    private static async Task<IResult> CreateAsync(
        CreateUserRequest request,
        UserManager<AppUser> userManager,
        CurrentUser current,
        IRealtimeNotifier notifier)
    {
        var validation = new Validation()
            .Required(request.Email, "email")
            .Required(request.Password, "password")
            .Required(request.DisplayName, "displayName")
            .MaxLength(request.DisplayName, 150, "displayName");

        if (!PlannerRoles.All.ContainsKey(request.Role))
        {
            validation.Add("role", $"Unknown role. Valid roles: {string.Join(", ", PlannerRoles.All.Keys)}.");
        }

        // Only an owner may mint another owner; admins must not be able to promote themselves sideways.
        if (request.Role == PlannerRoles.Owner && !current.IsOwner)
        {
            return ApiResults.Forbidden("Only the owner can grant the owner role.");
        }

        if (validation.HasErrors)
        {
            return validation.ToResult();
        }

        var user = new AppUser
        {
            Id = Guid.CreateVersion7(),
            UserName = request.Email,
            Email = request.Email,
            EmailConfirmed = true,
            DisplayName = request.DisplayName,
            TimeZone = request.TimeZone
        };

        var result = await userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            return IdentityProblem(result);
        }

        await userManager.AddToRoleAsync(user, request.Role);

        var summary = Mapping.ToUserSummary(user);
        await notifier.UserChanged(ChangeKind.Created, summary);
        return Results.Created($"/api/v1/users/{user.Id}", summary);
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateUserRequest request,
        UserManager<AppUser> userManager,
        CurrentUser current,
        IRealtimeNotifier notifier)
    {
        var user = await userManager.FindByIdAsync(id.ToString());
        if (user is null)
        {
            return ApiResults.NotFound("That user");
        }

        var roles = await userManager.GetRolesAsync(user);
        var currentRole = roles.FirstOrDefault() ?? PlannerRoles.Guest;

        if (request.Role.TryGet(out var newRole) && newRole != currentRole)
        {
            if (!PlannerRoles.All.ContainsKey(newRole!))
            {
                return new Validation()
                    .Add("role", $"Unknown role. Valid roles: {string.Join(", ", PlannerRoles.All.Keys)}.")
                    .ToResult();
            }

            if ((newRole == PlannerRoles.Owner || currentRole == PlannerRoles.Owner) && !current.IsOwner)
            {
                return ApiResults.Forbidden("Only the owner can grant or revoke the owner role.");
            }

            await userManager.RemoveFromRolesAsync(user, roles);
            await userManager.AddToRoleAsync(user, newRole!);
        }

        if (request.IsActive.TryGet(out var isActive) && isActive == false)
        {
            if (user.Id == current.Id)
            {
                return ApiResults.BadRequest("You cannot deactivate your own account.");
            }

            if (currentRole == PlannerRoles.Owner)
            {
                return ApiResults.Forbidden("The owner account cannot be deactivated. Transfer ownership first.");
            }
        }

        user.DisplayName = request.DisplayName.Or(user.DisplayName)!;
        user.AvatarUrl = request.AvatarUrl.Or(user.AvatarUrl);
        user.TimeZone = request.TimeZone.Or(user.TimeZone)!;
        user.IsActive = request.IsActive.Or(user.IsActive);

        var result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            return IdentityProblem(result);
        }

        var summary = Mapping.ToUserSummary(user);
        await notifier.UserChanged(ChangeKind.Updated, summary);
        return Results.Ok(summary);
    }

    private static async Task<IResult> ResetPasswordAsync(
        Guid id,
        ResetPasswordRequest request,
        UserManager<AppUser> userManager)
    {
        var user = await userManager.FindByIdAsync(id.ToString());
        if (user is null)
        {
            return ApiResults.NotFound("That user");
        }

        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var result = await userManager.ResetPasswordAsync(user, token, request.NewPassword);
        return result.Succeeded ? Results.NoContent() : IdentityProblem(result);
    }

    private static async Task<IResult> DeactivateAsync(
        Guid id,
        UserManager<AppUser> userManager,
        CurrentUser current,
        IRealtimeNotifier notifier)
    {
        var user = await userManager.FindByIdAsync(id.ToString());
        if (user is null)
        {
            return ApiResults.NotFound("That user");
        }

        if (user.Id == current.Id)
        {
            return ApiResults.BadRequest("You cannot deactivate your own account.");
        }

        var roles = await userManager.GetRolesAsync(user);
        if (roles.Contains(PlannerRoles.Owner))
        {
            return ApiResults.Forbidden("The owner account cannot be deactivated. Transfer ownership first.");
        }

        // Deactivate rather than delete: issues, comments and audit rows keep pointing at a real person.
        user.IsActive = false;
        var result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            return IdentityProblem(result);
        }

        await notifier.UserChanged(ChangeKind.Updated, Mapping.ToUserSummary(user));
        return Results.NoContent();
    }

    private static IResult IdentityProblem(IdentityResult result) =>
        Results.ValidationProblem(result.Errors
            .GroupBy(e => e.Code)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray()));
}
