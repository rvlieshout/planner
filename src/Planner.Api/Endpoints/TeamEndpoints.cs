using Microsoft.EntityFrameworkCore;
using Planner.Api.Auth;
using Planner.Api.Authorization;
using Planner.Api.Common;
using Planner.Api.Realtime;
using Planner.Contracts.Common;
using Planner.Contracts.Realtime;
using Planner.Contracts.Teams;
using Planner.Domain.Entities;
using Planner.Contracts.Enums;
using Planner.Infrastructure;

namespace Planner.Api.Endpoints;

public static class TeamEndpoints
{
    public static IEndpointRouteBuilder MapTeamEndpoints(this IEndpointRouteBuilder app)
    {
        var teams = app.MapGroup("/api/v1/teams").WithTags("Teams");

        teams.MapGet("/", ListAsync).WithSummary("Teams the caller can see");
        teams.MapGet("/{id:b58}", GetAsync).WithSummary("A single team");

        teams.MapPost("/", CreateAsync)
            .RequireAuthorization(PlannerPolicies.OrgAdmin)
            .WithSummary("Create a team, its default workflow states and its first member");

        teams.MapPatch("/{id:b58}", UpdateAsync).WithSummary("Update team settings");
        teams.MapPost("/{id:b58}/archive", ArchiveAsync).WithSummary("Archive a team");
        teams.MapPost("/{id:b58}/restore", RestoreAsync).WithSummary("Restore an archived team");

        teams.MapGet("/{id:b58}/members", ListMembersAsync).WithSummary("Team membership");
        teams.MapPost("/{id:b58}/members", AddMemberAsync).WithSummary("Add a user to the team");
        teams.MapPatch("/{id:b58}/members/{userId:b58}", UpdateMemberAsync).WithSummary("Change a member's team role");
        teams.MapDelete("/{id:b58}/members/{userId:b58}", RemoveMemberAsync).WithSummary("Remove a member");

        teams.MapGet("/{id:b58}/states", ListStatesAsync).WithSummary("Workflow states (board columns)");
        teams.MapPost("/{id:b58}/states", CreateStateAsync).WithSummary("Add a workflow state");
        teams.MapPatch("/{id:b58}/states/{stateId:b58}", UpdateStateAsync).WithSummary("Update a workflow state");
        teams.MapDelete("/{id:b58}/states/{stateId:b58}", DeleteStateAsync).WithSummary("Delete an empty workflow state");

        teams.MapGet("/{id:b58}/labels", ListTeamLabelsAsync).WithSummary("Labels usable by this team");
        teams.MapPost("/{id:b58}/labels", CreateTeamLabelAsync).WithSummary("Create a team label");

        var labels = app.MapGroup("/api/v1/labels").WithTags("Labels");
        labels.MapGet("/", ListLabelsAsync).WithSummary("All labels the caller can use");
        labels.MapPost("/", CreateOrgLabelAsync)
            .RequireAuthorization(PlannerPolicies.OrgAdmin)
            .WithSummary("Create an organisation-wide label");
        labels.MapPatch("/{labelId:b58}", UpdateLabelAsync).WithSummary("Update a label");
        labels.MapDelete("/{labelId:b58}", DeleteLabelAsync).WithSummary("Delete a label");

        return app;
    }

    private static async Task<IResult> ListAsync(
        PlannerDbContext db,
        ITeamAccess access,
        bool? includeArchived,
        CancellationToken ct)
    {
        var readable = await access.ReadableTeamIdsAsync(ct);

        var query = db.Teams.AsNoTracking().Where(t => readable.Contains(t.Id));

        if (includeArchived != true)
        {
            query = query.Where(t => t.ArchivedAt == null);
        }

        var items = await query.OrderBy(t => t.Key).Select(Mapping.TeamProjection).ToListAsync(ct);
        return Results.Ok(items);
    }

    private static async Task<IResult> GetAsync(Guid id, PlannerDbContext db, ITeamAccess access, CancellationToken ct)
    {
        if (await ApiResults.RequireTeamAsync(access, id, TeamPermission.Read, ct) is { } denied)
        {
            return denied;
        }

        var team = await db.Teams.AsNoTracking().Where(t => t.Id == id).Select(Mapping.TeamProjection)
            .FirstOrDefaultAsync(ct);

        return team is null ? ApiResults.NotFound("That team") : Results.Ok(team);
    }

    private static async Task<IResult> CreateAsync(
        CreateTeamRequest request,
        PlannerDbContext db,
        CurrentUser current,
        IRealtimeNotifier notifier,
        CancellationToken ct)
    {
        var key = request.Key?.Trim().ToUpperInvariant() ?? string.Empty;

        var validation = new Validation()
            .Required(key, "key")
            .MaxLength(key, 8, "key")
            .Matches(key, "^[A-Z][A-Z0-9]*$", "key", "key must start with a letter and contain only A-Z and 0-9.")
            .Required(request.Name, "name")
            .MaxLength(request.Name, 120, "name");

        if (validation.HasErrors)
        {
            return validation.ToResult();
        }

        if (await db.Teams.AnyAsync(t => t.Key == key, ct))
        {
            return ApiResults.Conflict($"A team with key {key} already exists.");
        }

        var team = new Team
        {
            Key = key,
            Name = request.Name.Trim(),
            Description = request.Description,
            Color = request.Color,
            IsPrivate = request.IsPrivate
        };

        db.Teams.Add(team);
        db.WorkflowStates.AddRange(WorkflowStateDefaults.CreateFor(team.Id));

        // The creator becomes the first lead: a team nobody can administer is a support ticket.
        db.TeamMembers.Add(new TeamMember { TeamId = team.Id, UserId = current.Id, Role = TeamRole.Lead });

        await db.SaveChangesAsync(ct);

        var dto = await db.Teams.AsNoTracking().Where(t => t.Id == team.Id).Select(Mapping.TeamProjection).FirstAsync(ct);
        await notifier.TeamChanged(ChangeKind.Created, dto);
        return Results.Created($"/api/v1/teams/{team.Id.ToBase58()}", dto);
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateTeamRequest request,
        PlannerDbContext db,
        ITeamAccess access,
        IRealtimeNotifier notifier,
        CancellationToken ct)
    {
        if (await ApiResults.RequireTeamAsync(access, id, TeamPermission.Administer, ct) is { } denied)
        {
            return denied;
        }

        var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (team is null)
        {
            return ApiResults.NotFound("That team");
        }

        if (request.Name.TryGet(out var name))
        {
            var validation = new Validation().Required(name, "name").MaxLength(name, 120, "name");
            if (validation.HasErrors)
            {
                return validation.ToResult();
            }
        }

        team.Name = request.Name.Or(team.Name)!;
        team.Description = request.Description.Or(team.Description);
        team.Color = request.Color.Or(team.Color)!;
        team.IsPrivate = request.IsPrivate.Or(team.IsPrivate);

        await db.SaveChangesAsync(ct);

        var dto = await db.Teams.AsNoTracking().Where(t => t.Id == id).Select(Mapping.TeamProjection).FirstAsync(ct);
        await notifier.TeamChanged(ChangeKind.Updated, dto);
        return Results.Ok(dto);
    }

    private static Task<IResult> ArchiveAsync(
        Guid id,
        PlannerDbContext db,
        ITeamAccess access,
        IRealtimeNotifier notifier,
        CancellationToken ct) => SetArchivedAsync(id, db, access, notifier, DateTimeOffset.UtcNow, ct);

    private static Task<IResult> RestoreAsync(
        Guid id,
        PlannerDbContext db,
        ITeamAccess access,
        IRealtimeNotifier notifier,
        CancellationToken ct) => SetArchivedAsync(id, db, access, notifier, null, ct);

    private static async Task<IResult> SetArchivedAsync(
        Guid id,
        PlannerDbContext db,
        ITeamAccess access,
        IRealtimeNotifier notifier,
        DateTimeOffset? archivedAt,
        CancellationToken ct)
    {
        if (await ApiResults.RequireTeamAsync(access, id, TeamPermission.Administer, ct) is { } denied)
        {
            return denied;
        }

        var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (team is null)
        {
            return ApiResults.NotFound("That team");
        }

        team.ArchivedAt = archivedAt;
        await db.SaveChangesAsync(ct);

        var dto = await db.Teams.AsNoTracking().Where(t => t.Id == id).Select(Mapping.TeamProjection).FirstAsync(ct);
        await notifier.TeamChanged(archivedAt is null ? ChangeKind.Restored : ChangeKind.Archived, dto);
        return Results.Ok(dto);
    }

    private static async Task<IResult> ListMembersAsync(
        Guid id,
        PlannerDbContext db,
        ITeamAccess access,
        CancellationToken ct)
    {
        if (await ApiResults.RequireTeamAsync(access, id, TeamPermission.Read, ct) is { } denied)
        {
            return denied;
        }

        var members = await db.TeamMembers.AsNoTracking()
            .Where(m => m.TeamId == id)
            .OrderByDescending(m => m.Role)
            .ThenBy(m => m.User.DisplayName)
            .Select(Mapping.TeamMemberProjection)
            .ToListAsync(ct);

        return Results.Ok(members);
    }

    private static async Task<IResult> AddMemberAsync(
        Guid id,
        AddTeamMemberRequest request,
        PlannerDbContext db,
        ITeamAccess access,
        IActivityLog activity,
        IRealtimeNotifier notifier,
        IRealtimeSubscriptions subscriptions,
        CancellationToken ct)
    {
        if (await ApiResults.RequireTeamAsync(access, id, TeamPermission.Administer, ct) is { } denied)
        {
            return denied;
        }

        if (!await db.Users.AnyAsync(u => u.Id == request.UserId, ct))
        {
            return ApiResults.NotFound("That user");
        }

        if (await db.TeamMembers.AnyAsync(m => m.TeamId == id && m.UserId == request.UserId, ct))
        {
            return ApiResults.Conflict("That user is already a member of this team.");
        }

        db.TeamMembers.Add(new TeamMember { TeamId = id, UserId = request.UserId, Role = request.Role });
        activity.Record(EntityTypes.TeamMember, request.UserId, ActivityActions.MemberAdded,
            new { role = request.Role.ToString() }, teamId: id);

        await db.SaveChangesAsync(ct);

        var dto = await db.TeamMembers.AsNoTracking()
            .Where(m => m.TeamId == id && m.UserId == request.UserId)
            .Select(Mapping.TeamMemberProjection)
            .FirstAsync(ct);

        // Joined first, so the new member's open sockets receive the announcement of their own arrival.
        await subscriptions.SyncUserAsync(request.UserId);
        await notifier.TeamMemberChanged(ChangeKind.Created, dto);
        return Results.Created($"/api/v1/teams/{id.ToBase58()}/members/{request.UserId.ToBase58()}", dto);
    }

    private static async Task<IResult> UpdateMemberAsync(
        Guid id,
        Guid userId,
        UpdateTeamMemberRequest request,
        PlannerDbContext db,
        ITeamAccess access,
        IActivityLog activity,
        IRealtimeNotifier notifier,
        CancellationToken ct)
    {
        if (await ApiResults.RequireTeamAsync(access, id, TeamPermission.Administer, ct) is { } denied)
        {
            return denied;
        }

        var member = await db.TeamMembers.FirstOrDefaultAsync(m => m.TeamId == id && m.UserId == userId, ct);
        if (member is null)
        {
            return ApiResults.NotFound("That membership");
        }

        // Refuse to remove the last lead, otherwise the team becomes unmanageable by its own members.
        if (member.Role == TeamRole.Lead && request.Role != TeamRole.Lead &&
            !await db.TeamMembers.AnyAsync(m => m.TeamId == id && m.UserId != userId && m.Role == TeamRole.Lead, ct))
        {
            return ApiResults.Conflict("This is the team's only lead. Promote another member first.");
        }

        var previous = member.Role;
        member.Role = request.Role;

        activity.Record(EntityTypes.TeamMember, userId, ActivityActions.MemberRoleChanged,
            new { from = previous.ToString(), to = request.Role.ToString() }, teamId: id);

        await db.SaveChangesAsync(ct);

        var dto = await db.TeamMembers.AsNoTracking()
            .Where(m => m.TeamId == id && m.UserId == userId)
            .Select(Mapping.TeamMemberProjection)
            .FirstAsync(ct);

        await notifier.TeamMemberChanged(ChangeKind.Updated, dto);
        return Results.Ok(dto);
    }

    private static async Task<IResult> RemoveMemberAsync(
        Guid id,
        Guid userId,
        PlannerDbContext db,
        ITeamAccess access,
        IActivityLog activity,
        IRealtimeNotifier notifier,
        IRealtimeSubscriptions subscriptions,
        CancellationToken ct)
    {
        if (await ApiResults.RequireTeamAsync(access, id, TeamPermission.Administer, ct) is { } denied)
        {
            return denied;
        }

        var member = await db.TeamMembers
            .Include(m => m.User)
            .FirstOrDefaultAsync(m => m.TeamId == id && m.UserId == userId, ct);

        if (member is null)
        {
            return ApiResults.NotFound("That membership");
        }

        if (member.Role == TeamRole.Lead &&
            !await db.TeamMembers.AnyAsync(m => m.TeamId == id && m.UserId != userId && m.Role == TeamRole.Lead, ct))
        {
            return ApiResults.Conflict("This is the team's only lead. Promote another member first.");
        }

        var dto = Mapping.ToTeamMember(member);

        db.TeamMembers.Remove(member);
        activity.Record(EntityTypes.TeamMember, userId, ActivityActions.MemberRemoved, null, teamId: id);
        await db.SaveChangesAsync(ct);

        await notifier.TeamMemberChanged(ChangeKind.Deleted, dto);

        // Left afterwards, so the removed member still hears that they were removed.
        await subscriptions.SyncUserAsync(userId);
        return Results.NoContent();
    }

    private static async Task<IResult> ListStatesAsync(
        Guid id,
        PlannerDbContext db,
        ITeamAccess access,
        CancellationToken ct)
    {
        if (await ApiResults.RequireTeamAsync(access, id, TeamPermission.Read, ct) is { } denied)
        {
            return denied;
        }

        var states = await db.WorkflowStates.AsNoTracking()
            .Where(s => s.TeamId == id)
            .OrderBy(s => s.Position)
            .Select(Mapping.WorkflowStateProjection)
            .ToListAsync(ct);

        return Results.Ok(states);
    }

    private static async Task<IResult> CreateStateAsync(
        Guid id,
        CreateWorkflowStateRequest request,
        PlannerDbContext db,
        ITeamAccess access,
        IRealtimeNotifier notifier,
        CancellationToken ct)
    {
        if (await ApiResults.RequireTeamAsync(access, id, TeamPermission.Administer, ct) is { } denied)
        {
            return denied;
        }

        var validation = new Validation().Required(request.Name, "name").MaxLength(request.Name, 60, "name");
        if (validation.HasErrors)
        {
            return validation.ToResult();
        }

        if (await db.WorkflowStates.AnyAsync(s => s.TeamId == id && s.Name == request.Name, ct))
        {
            return ApiResults.Conflict($"This team already has a state named {request.Name}.");
        }

        var position = request.Position
                       ?? await db.WorkflowStates.Where(s => s.TeamId == id).MaxAsync(s => (int?)s.Position, ct) + 1
                       ?? 0;

        var state = new WorkflowState
        {
            TeamId = id,
            Name = request.Name.Trim(),
            Type = request.Type,
            Color = request.Color,
            Position = position,
            IsDefault = request.IsDefault
        };

        if (request.IsDefault)
        {
            await ClearDefaultStateAsync(db, id, ct);
        }

        db.WorkflowStates.Add(state);
        await db.SaveChangesAsync(ct);

        var dto = Mapping.ToWorkflowState(state);
        await notifier.WorkflowStateChanged(ChangeKind.Created, dto);
        return Results.Created($"/api/v1/teams/{id.ToBase58()}/states/{state.Id.ToBase58()}", dto);
    }

    private static async Task<IResult> UpdateStateAsync(
        Guid id,
        Guid stateId,
        UpdateWorkflowStateRequest request,
        PlannerDbContext db,
        ITeamAccess access,
        IRealtimeNotifier notifier,
        CancellationToken ct)
    {
        if (await ApiResults.RequireTeamAsync(access, id, TeamPermission.Administer, ct) is { } denied)
        {
            return denied;
        }

        var state = await db.WorkflowStates.FirstOrDefaultAsync(s => s.Id == stateId && s.TeamId == id, ct);
        if (state is null)
        {
            return ApiResults.NotFound("That workflow state");
        }

        // The same rules a new state is held to, or a rename could empty a column's name or give two
        // columns the same one.
        if (request.Name.TryGet(out var name))
        {
            var validation = new Validation().Required(name, "name").MaxLength(name, 60, "name");
            if (validation.HasErrors)
            {
                return validation.ToResult();
            }

            name = name!.Trim();
            if (await db.WorkflowStates.AnyAsync(s => s.TeamId == id && s.Name == name && s.Id != stateId, ct))
            {
                return ApiResults.Conflict($"This team already has a state named {name}.");
            }

            state.Name = name;
        }

        if (request.IsDefault.TryGet(out var isDefault) && isDefault)
        {
            await ClearDefaultStateAsync(db, id, ct);
        }
        state.Type = request.Type.Or(state.Type);
        state.Color = request.Color.Or(state.Color)!;
        state.Position = request.Position.Or(state.Position);
        state.IsDefault = request.IsDefault.Or(state.IsDefault);

        await db.SaveChangesAsync(ct);

        var dto = Mapping.ToWorkflowState(state);
        await notifier.WorkflowStateChanged(ChangeKind.Updated, dto);
        return Results.Ok(dto);
    }

    private static async Task<IResult> DeleteStateAsync(
        Guid id,
        Guid stateId,
        PlannerDbContext db,
        ITeamAccess access,
        IRealtimeNotifier notifier,
        CancellationToken ct)
    {
        if (await ApiResults.RequireTeamAsync(access, id, TeamPermission.Administer, ct) is { } denied)
        {
            return denied;
        }

        var state = await db.WorkflowStates.FirstOrDefaultAsync(s => s.Id == stateId && s.TeamId == id, ct);
        if (state is null)
        {
            return ApiResults.NotFound("That workflow state");
        }

        if (await db.Issues.AnyAsync(i => i.StateId == stateId, ct))
        {
            return ApiResults.Conflict("Issues still sit in this state. Move them to another state first.");
        }

        if (await db.WorkflowStates.CountAsync(s => s.TeamId == id, ct) <= 1)
        {
            return ApiResults.Conflict("A team needs at least one workflow state.");
        }

        var dto = Mapping.ToWorkflowState(state);
        db.WorkflowStates.Remove(state);
        await db.SaveChangesAsync(ct);

        await notifier.WorkflowStateChanged(ChangeKind.Deleted, dto);
        return Results.NoContent();
    }

    private static async Task ClearDefaultStateAsync(PlannerDbContext db, Guid teamId, CancellationToken ct)
    {
        await db.WorkflowStates
            .Where(s => s.TeamId == teamId && s.IsDefault)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsDefault, false), ct);
    }

    private static async Task<IResult> ListTeamLabelsAsync(
        Guid id,
        PlannerDbContext db,
        ITeamAccess access,
        CancellationToken ct)
    {
        if (await ApiResults.RequireTeamAsync(access, id, TeamPermission.Read, ct) is { } denied)
        {
            return denied;
        }

        var labels = await db.Labels.AsNoTracking()
            .Where(l => l.TeamId == id || l.TeamId == null)
            .OrderBy(l => l.TeamId == null ? 0 : 1)
            .ThenBy(l => l.Name)
            .Select(Mapping.LabelProjection)
            .ToListAsync(ct);

        return Results.Ok(labels);
    }

    private static async Task<IResult> ListLabelsAsync(
        PlannerDbContext db,
        ITeamAccess access,
        CancellationToken ct)
    {
        var readable = await access.ReadableTeamIdsAsync(ct);

        var labels = await db.Labels.AsNoTracking()
            .Where(l => l.TeamId == null || readable.Contains(l.TeamId!.Value))
            .OrderBy(l => l.TeamId == null ? 0 : 1)
            .ThenBy(l => l.Name)
            .Select(Mapping.LabelProjection)
            .ToListAsync(ct);

        return Results.Ok(labels);
    }

    private static async Task<IResult> CreateTeamLabelAsync(
        Guid id,
        CreateLabelRequest request,
        PlannerDbContext db,
        ITeamAccess access,
        IRealtimeNotifier notifier,
        CancellationToken ct)
    {
        if (await ApiResults.RequireTeamAsync(access, id, TeamPermission.Administer, ct) is { } denied)
        {
            return denied;
        }

        return await CreateLabelAsync(id, request, db, notifier, ct);
    }

    private static Task<IResult> CreateOrgLabelAsync(
        CreateLabelRequest request,
        PlannerDbContext db,
        IRealtimeNotifier notifier,
        CancellationToken ct) => CreateLabelAsync(null, request, db, notifier, ct);

    private static async Task<IResult> CreateLabelAsync(
        Guid? teamId,
        CreateLabelRequest request,
        PlannerDbContext db,
        IRealtimeNotifier notifier,
        CancellationToken ct)
    {
        var name = request.Name?.Trim();
        var validation = ValidateLabel(name, request.Color, request.Description);
        if (validation.HasErrors)
        {
            return validation.ToResult();
        }

        if (await db.Labels.AnyAsync(l => l.TeamId == teamId && l.Name == name, ct))
        {
            return ApiResults.Conflict($"A label named {name} already exists in this scope.");
        }

        var label = new Label
        {
            TeamId = teamId,
            Name = name!,
            Color = request.Color,
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim()
        };

        db.Labels.Add(label);
        await db.SaveChangesAsync(ct);

        var dto = Mapping.ToLabel(label);

        if (teamId is { } scoped)
        {
            await notifier.LabelChanged(ChangeKind.Created, dto, scoped);
        }

        return Results.Created($"/api/v1/labels/{label.Id.ToBase58()}", dto);
    }

    private static Validation ValidateLabel(string? name, string? color, string? description) =>
        new Validation()
            .Required(name, "name")
            .MaxLength(name, 60, "name")
            .Required(color, "color")
            .Matches(color, "^#([0-9A-Fa-f]{3}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$", "color", "color must be a hex colour such as #5E6AD2.")
            .MaxLength(description, 500, "description");

    private static async Task<IResult> UpdateLabelAsync(
        Guid labelId,
        UpdateLabelRequest request,
        PlannerDbContext db,
        ITeamAccess access,
        CurrentUser current,
        IRealtimeNotifier notifier,
        CancellationToken ct)
    {
        var label = await db.Labels.FirstOrDefaultAsync(l => l.Id == labelId, ct);
        if (label is null)
        {
            return ApiResults.NotFound("That label");
        }

        if (label.TeamId is { } teamId)
        {
            if (await ApiResults.RequireTeamAsync(access, teamId, TeamPermission.Administer, ct) is { } denied)
            {
                return denied;
            }
        }
        else if (!current.IsAdmin)
        {
            return ApiResults.Forbidden("Organisation-wide labels are managed by administrators.");
        }

        var name = request.Name.Or(label.Name)?.Trim();
        var color = request.Color.Or(label.Color);
        var description = request.Description.Or(label.Description);

        var validation = ValidateLabel(name, color, description);
        if (validation.HasErrors)
        {
            return validation.ToResult();
        }

        if (name != label.Name &&
            await db.Labels.AnyAsync(l => l.Id != label.Id && l.TeamId == label.TeamId && l.Name == name, ct))
        {
            return ApiResults.Conflict($"A label named {name} already exists in this scope.");
        }

        label.Name = name!;
        label.Color = color!;
        label.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();

        await db.SaveChangesAsync(ct);

        var dto = Mapping.ToLabel(label);
        if (label.TeamId is { } scoped)
        {
            await notifier.LabelChanged(ChangeKind.Updated, dto, scoped);
        }

        return Results.Ok(dto);
    }

    private static async Task<IResult> DeleteLabelAsync(
        Guid labelId,
        PlannerDbContext db,
        ITeamAccess access,
        CurrentUser current,
        IRealtimeNotifier notifier,
        CancellationToken ct)
    {
        var label = await db.Labels.FirstOrDefaultAsync(l => l.Id == labelId, ct);
        if (label is null)
        {
            return ApiResults.NotFound("That label");
        }

        if (label.TeamId is { } teamId)
        {
            if (await ApiResults.RequireTeamAsync(access, teamId, TeamPermission.Administer, ct) is { } denied)
            {
                return denied;
            }
        }
        else if (!current.IsAdmin)
        {
            return ApiResults.Forbidden("Organisation-wide labels are managed by administrators.");
        }

        var dto = Mapping.ToLabel(label);
        db.Labels.Remove(label);
        await db.SaveChangesAsync(ct);

        if (label.TeamId is { } scoped)
        {
            await notifier.LabelChanged(ChangeKind.Deleted, dto, scoped);
        }

        return Results.NoContent();
    }
}
