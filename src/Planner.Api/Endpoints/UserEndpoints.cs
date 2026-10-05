using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Planner.Api.Auth;
using Planner.Api.Authorization;
using Planner.Api.Common;
using Planner.Api.Realtime;
using Planner.Contracts.Auth;
using Planner.Contracts.Common;
using Planner.Contracts.Enums;
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

        users.MapGet("/{id:b58}", GetAsync)
            .WithSummary("A single user");

        users.MapPost("/", CreateAsync)
            .RequireAuthorization(PlannerPolicies.OrgAdmin)
            .WithSummary("Create a user account");

        users.MapPatch("/{id:b58}", UpdateAsync)
            .RequireAuthorization(PlannerPolicies.OrgAdmin)
            .WithSummary("Update a user's profile, role or active state");

        users.MapPost("/{id:b58}/password", ResetPasswordAsync)
            .RequireAuthorization(PlannerPolicies.OrgAdmin)
            .WithSummary("Set a user's password without knowing the current one");

        users.MapDelete("/{id:b58}", DeactivateAsync)
            .RequireAuthorization(PlannerPolicies.OrgAdmin)
            .WithSummary("Deactivate a user; authored content is kept. With permanent=true, delete one that never signed in");

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
        RealtimeNotifier notifier,
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

        // Every colleague's browser fetches this address whenever the person's name is on screen, so one
        // chosen freely is a beacon: it tells its owner who is looking, from where and when. Pictures are
        // therefore pointed at by an administrator; a person can only take their own down.
        if (request.AvatarUrl.TryGet(out var avatarUrl) && avatarUrl is not null && avatarUrl != user.AvatarUrl)
        {
            validation.Add("avatarUrl", "avatarUrl is set by an administrator. You can clear your own by sending null.");
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

    /// <summary>An address a browser can load a picture from: http or https, or a path on this origin.</summary>
    public static bool IsAvatarUrl(string value) =>
        value.Length <= 2000 &&
        (IssueEndpoints.IsWebLink(value) ||
         (value.StartsWith('/') && !value.StartsWith("//", StringComparison.Ordinal) && !value.Contains('\\')));

    /// <summary>The accounts the caller may look up. Administrators see every account. Everyone else
    /// sees the active ones, which is what a picker offers: an invitation that is still pending, or an
    /// account that was closed, is administration's business. A guest sees only the people they share a
    /// team with, and themselves: they were let into those teams, not into the organisation.</summary>
    private static IQueryable<AppUser> VisibleUsers(PlannerDbContext db, CurrentUser current)
    {
        var users = db.Users.AsNoTracking();

        if (current.IsAdmin)
        {
            return users;
        }

        var callerId = current.Id;
        users = users.Where(u => u.IsActive || u.Id == callerId);

        return current.IsGuest
            ? users.Where(u => u.Id == callerId || u.TeamMemberships.Any(theirs =>
                db.TeamMembers.Any(mine => mine.UserId == callerId && mine.TeamId == theirs.TeamId)))
            : users;
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
        CurrentUser current,
        [AsParameters] PageQuery paging,
        string? search,
        bool? includeInactive,
        CancellationToken ct)
    {
        var query = VisibleUsers(db, current);

        // Only an administrator has inactive accounts to include; for anyone else the flag changes nothing.
        if (includeInactive != true || !current.IsAdmin)
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
        CurrentUser current,
        CancellationToken ct)
    {
        var user = await VisibleUsers(db, current).FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return ApiResults.NotFound("That user");
        }

        var roles = await userManager.GetRolesAsync(user);

        // When someone was last here is for the people who manage accounts, and for the person themselves.
        var seesActivity = current.IsAdmin || user.Id == current.Id;

        return Results.Ok(new UserDetail(
            user.Id,
            user.Email!,
            user.DisplayName,
            user.AvatarUrl,
            user.TimeZone,
            roles.FirstOrDefault() ?? PlannerRoles.Guest,
            user.IsActive,
            user.CreatedAt,
            seesActivity ? user.LastSeenAt : null,
            user.IsInvitationPending,
            user.InvitationExpiresAt));
    }

    private static async Task<IResult> CreateAsync(
        CreateUserRequest request,
        UserManager<AppUser> userManager,
        CurrentUser current,
        RealtimeNotifier notifier)
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
        return Results.Created($"/api/v1/users/{user.Id.ToBase58()}", summary);
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateUserRequest request,
        UserManager<AppUser> userManager,
        CurrentUser current,
        RealtimeNotifier notifier,
        RealtimeSubscriptions subscriptions)
    {
        var user = await userManager.FindByIdAsync(id.ToString());
        if (user is null)
        {
            return ApiResults.NotFound("That user");
        }

        var roles = await userManager.GetRolesAsync(user);
        var currentRole = roles.FirstOrDefault() ?? PlannerRoles.Guest;

        if (user.IsInvitationPending && request.IsActive.TryGet(out var activate) && activate)
        {
            return ApiResults.BadRequest("Pending users must accept their invitation before activation.");
        }

        // An address mistyped on the invitation would otherwise hold the account for good. Once someone
        // has accepted, the address is how they sign in and stays put.
        var newEmail = request.Email.TryGet(out var email) ? email?.Trim() : user.Email;
        if (newEmail != user.Email)
        {
            if (!user.IsInvitationPending)
            {
                return ApiResults.BadRequest("An email address can only be changed while the invitation is pending.");
            }

            var validation = new Validation().Required(newEmail, "email");
            if (!validation.HasErrors && await userManager.FindByEmailAsync(newEmail!) is { } taken && taken.Id != user.Id)
            {
                validation.Add("email", "Another account already uses this email address.");
            }

            if (validation.HasErrors)
            {
                return validation.ToResult();
            }
        }

        if (request.AvatarUrl.TryGet(out var avatarUrl) && avatarUrl is not null && !IsAvatarUrl(avatarUrl))
        {
            return new Validation()
                .Add("avatarUrl", "avatarUrl must be an http or https address, or a path on this server, of at most 2000 characters.")
                .ToResult();
        }

        // Checked here and applied at the end, with everything else: a request that is refused further
        // down must not have changed the role on its way there.
        var changesRole = request.Role.TryGet(out var newRole) && newRole != currentRole;
        if (changesRole)
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

            // Only an owner can make another owner, so an installation without one stays without one.
            if (currentRole == PlannerRoles.Owner && user.IsActive &&
                !(await userManager.GetUsersInRoleAsync(PlannerRoles.Owner)).Any(o => o.Id != user.Id && o.IsActive))
            {
                return ApiResults.Conflict("This is the only owner. Grant the owner role to someone else first.");
            }
        }

        if (request.IsActive.TryGet(out var isActive) && isActive == false)
        {
            if (user.Id == current.Id)
            {
                return ApiResults.BadRequest("You cannot deactivate your own account.");
            }

            if (currentRole == PlannerRoles.Owner && !(user.IsInvitationPending && current.IsOwner))
            {
                return ApiResults.Forbidden("The owner account cannot be deactivated. Transfer ownership first.");
            }
        }

        user.DisplayName = request.DisplayName.Or(user.DisplayName)!;
        user.AvatarUrl = request.AvatarUrl.Or(user.AvatarUrl);
        user.TimeZone = request.TimeZone.Or(user.TimeZone)!;
        user.IsActive = request.IsActive.Or(user.IsActive);
        // Links already shared keep working: they name the account, not its address.
        user.Email = newEmail;
        user.UserName = newEmail;

        if (user.IsInvitationPending && request.IsActive.TryGet(out var deactivate) && !deactivate)
        {
            user.SecurityStamp = Guid.NewGuid().ToString();
            user.InvitationExpiresAt = null;
        }

        var result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            return IdentityProblem(result);
        }

        if (changesRole)
        {
            result = await userManager.RemoveFromRolesAsync(user, roles);
            if (result.Succeeded)
            {
                result = await userManager.AddToRoleAsync(user, newRole!);
            }

            if (!result.Succeeded)
            {
                return IdentityProblem(result);
            }
        }

        var summary = Mapping.ToUserSummary(user);
        await notifier.UserChanged(ChangeKind.Updated, summary);

        // The organisation role decides whether every team is readable or only the user's own, and a
        // deactivated account reads none. Their open sockets follow now rather than at reconnect.
        await subscriptions.SyncUserAsync(user.Id);
        return Results.Ok(summary);
    }

    private static async Task<IResult> ResetPasswordAsync(
        Guid id,
        ResetPasswordRequest request,
        UserManager<AppUser> userManager,
        CurrentUser current,
        ILoggerFactory loggers)
    {
        var user = await userManager.FindByIdAsync(id.ToString());
        if (user is null)
        {
            return ApiResults.NotFound("That user");
        }

        // Setting someone's password is being able to sign in as them, so an admin who could do it to
        // the owner would hold the owner role in all but name.
        if (!current.IsOwner && await userManager.IsInRoleAsync(user, PlannerRoles.Owner))
        {
            return ApiResults.Forbidden("Only the owner can reset an owner's password.");
        }

        if (user.IsInvitationPending)
        {
            return ApiResults.BadRequest("Pending users must choose their own password by accepting an invitation.");
        }

        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var result = await userManager.ResetPasswordAsync(user, token, request.NewPassword);
        if (!result.Succeeded)
        {
            return IdentityProblem(result);
        }

        // The activity feed is per team and this belongs to none, so the record of who did it is the log.
        loggers.CreateLogger("Planner.Auth.Users").LogInformation(
            "User {ActorId} reset the password of user {UserId}", current.Id, user.Id);
        return Results.NoContent();
    }

    private static async Task<IResult> DeactivateAsync(
        Guid id,
        bool? permanent,
        PlannerDbContext db,
        UserManager<AppUser> userManager,
        CurrentUser current,
        RealtimeNotifier notifier,
        RealtimeSubscriptions subscriptions,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var user = await userManager.FindByIdAsync(id.ToString());
        if (user is null)
        {
            return ApiResults.NotFound("That user");
        }

        if (permanent == true)
        {
            return await DeleteAsync(user, db, userManager, current, notifier, loggers, ct);
        }

        if (user.Id == current.Id)
        {
            return ApiResults.BadRequest("You cannot deactivate your own account.");
        }

        var roles = await userManager.GetRolesAsync(user);
        if (roles.Contains(PlannerRoles.Owner) && !(user.IsInvitationPending && current.IsOwner))
        {
            return ApiResults.Forbidden("The owner account cannot be deactivated. Transfer ownership first.");
        }

        // Deactivate rather than delete: issues, comments and audit rows keep pointing at a real person.
        user.IsActive = false;
        // Cancelling a pending invitation must invalidate its bearer link as well.
        if (user.IsInvitationPending)
        {
            user.SecurityStamp = Guid.NewGuid().ToString();
            user.InvitationExpiresAt = null;
        }
        var result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            return IdentityProblem(result);
        }

        await notifier.UserChanged(ChangeKind.Updated, Mapping.ToUserSummary(user));
        await subscriptions.SyncUserAsync(user.Id);
        return Results.NoContent();
    }

    /// <summary>Removes an account outright. Only one nobody has ever signed in to: an invitation sent
    /// to the wrong person, or an account made and never used. Every way of obtaining a token stamps
    /// <see cref="AppUser.LastSeenAt"/>, so an account without one has done nothing that needs an author.
    /// What others did with it goes with it: team memberships and followed issues are removed, and
    /// issues assigned to it and projects it leads are left without one.</summary>
    private static async Task<IResult> DeleteAsync(
        AppUser user,
        PlannerDbContext db,
        UserManager<AppUser> userManager,
        CurrentUser current,
        RealtimeNotifier notifier,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        if (!current.IsOwner && await userManager.IsInRoleAsync(user, PlannerRoles.Owner))
        {
            return ApiResults.Forbidden("Only the owner can delete an owner account.");
        }

        if (user.LastSeenAt is not null)
        {
            return ApiResults.Conflict("This account has been signed in to, so it can only be deactivated.");
        }

        // Seeded and imported content can sit under an account nobody has used. The database would
        // refuse the delete anyway; this says why.
        var userId = user.Id;
        if (await db.Issues.AnyAsync(i => i.CreatorId == userId, ct) ||
            await db.Comments.AnyAsync(c => c.AuthorId == userId, ct) ||
            await db.Attachments.AnyAsync(a => a.UploadedById == userId, ct) ||
            await db.Documents.AnyAsync(d => d.CreatedById == userId, ct) ||
            await db.ActivityEvents.AnyAsync(a => a.ActorId == userId, ct))
        {
            return ApiResults.Conflict("Content is recorded under this account, so it can only be deactivated.");
        }

        // The same rule as removing a member: a team is not left without a lead.
        var leaderless = await db.TeamMembers
            .Where(m => m.UserId == userId && m.Role == TeamRole.Lead &&
                        !m.Team.Members.Any(o => o.UserId != userId && o.Role == TeamRole.Lead))
            .Select(m => m.Team.Name)
            .ToListAsync(ct);
        if (leaderless.Count > 0)
        {
            return ApiResults.Conflict(
                $"This account is the only lead of {string.Join(", ", leaderless)}. Promote another member first.");
        }

        var memberships = await db.TeamMembers.AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(Mapping.TeamMemberProjection)
            .ToListAsync(ct);

        // The database clears these references itself as the account goes, so nothing passes through
        // here to announce. They are noted now, and read back and broadcast once it has.
        var assignedIssueIds = await db.Issues.Where(i => i.AssigneeId == userId).Select(i => i.Id).ToListAsync(ct);
        var ledProjectIds = await db.Projects.Where(p => p.LeadUserId == userId).Select(p => p.Id).ToListAsync(ct);

        var summary = Mapping.ToUserSummary(user);

        // A sign-in that lands between the check above and here changes the row's concurrency stamp,
        // and Identity then refuses the delete.
        var result = await userManager.DeleteAsync(user);
        if (!result.Succeeded)
        {
            return IdentityProblem(result);
        }

        // The activity feed is per team and this belongs to none, so the record of who did it is the log.
        loggers.CreateLogger("Planner.Auth.Users").LogInformation(
            "User {ActorId} deleted user {UserId}, who had never signed in", current.Id, userId);

        foreach (var membership in memberships)
        {
            await notifier.TeamMemberChanged(ChangeKind.Deleted, membership);
        }

        // Past this point the account is gone whatever happens, so a caller hanging up must not cut
        // the announcements short.
        var unassigned = await db.Issues.AsNoTracking()
            .Where(i => assignedIssueIds.Contains(i.Id))
            .Select(Mapping.IssueSummaryProjection)
            .ToListAsync(CancellationToken.None);
        foreach (var issue in unassigned)
        {
            await notifier.IssueChanged(ChangeKind.Updated, issue);
        }

        var leaderlessProjects = await db.Projects.AsNoTracking()
            .Where(p => ledProjectIds.Contains(p.Id))
            .Select(Mapping.ProjectProjection)
            .ToListAsync(CancellationToken.None);
        foreach (var project in leaderlessProjects)
        {
            await notifier.ProjectChanged(ChangeKind.Updated, project);
        }

        await notifier.UserChanged(ChangeKind.Deleted, summary);
        return Results.NoContent();
    }

    private static IResult IdentityProblem(IdentityResult result) =>
        Results.ValidationProblem(result.Errors
            .GroupBy(e => e.Code)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray()));
}
