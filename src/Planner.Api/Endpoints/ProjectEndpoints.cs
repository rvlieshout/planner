using Microsoft.EntityFrameworkCore;
using Planner.Api.Authorization;
using Planner.Api.Common;
using Planner.Api.Realtime;
using Planner.Contracts.Common;
using Planner.Contracts.Projects;
using Planner.Contracts.Realtime;
using Planner.Domain.Entities;
using Planner.Domain.Enums;
using Planner.Infrastructure;

namespace Planner.Api.Endpoints;

public static class ProjectEndpoints
{
    public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        var projects = app.MapGroup("/api/v1/projects").WithTags("Projects");

        projects.MapGet("/", ListAsync).WithSummary("Projects across the teams the caller can see");
        projects.MapGet("/{id:guid}", GetAsync).WithSummary("A single project with its issue rollup");
        projects.MapPost("/", CreateAsync).WithSummary("Create a project");
        projects.MapPatch("/{id:guid}", UpdateAsync).WithSummary("Update a project");
        projects.MapPost("/{id:guid}/archive", ArchiveAsync).WithSummary("Archive a project");
        projects.MapPost("/{id:guid}/restore", RestoreAsync).WithSummary("Restore an archived project");
        projects.MapDelete("/{id:guid}", DeleteAsync).WithSummary("Delete a project; its issues stay in the team");

        projects.MapGet("/{id:guid}/milestones", ListMilestonesAsync).WithSummary("Milestones of a project");
        projects.MapPost("/{id:guid}/milestones", CreateMilestoneAsync).WithSummary("Add a milestone");

        var milestones = app.MapGroup("/api/v1/milestones").WithTags("Milestones");
        milestones.MapGet("/{milestoneId:guid}", GetMilestoneAsync).WithSummary("A single milestone");
        milestones.MapPatch("/{milestoneId:guid}", UpdateMilestoneAsync).WithSummary("Update a milestone");
        milestones.MapDelete("/{milestoneId:guid}", DeleteMilestoneAsync).WithSummary("Delete a milestone");

        return app;
    }

    private static async Task<IResult> ListAsync(
        PlannerDbContext db,
        ITeamAccess access,
        [AsParameters] PageQuery paging,
        Guid? teamId,
        ProjectStatus[]? status,
        bool? includeArchived,
        string? search,
        CancellationToken ct)
    {
        var readable = await access.ReadableTeamIdsAsync(ct);

        var query = db.Projects.AsNoTracking().Where(p => readable.Contains(p.TeamId));

        if (teamId is { } scoped)
        {
            query = query.Where(p => p.TeamId == scoped);
        }

        if (status is { Length: > 0 })
        {
            query = query.Where(p => status.Contains(p.Status));
        }

        if (includeArchived != true)
        {
            query = query.Where(p => p.ArchivedAt == null);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(p => EF.Functions.ILike(p.Name, pattern));
        }

        var total = await query.LongCountAsync(ct);
        var items = await query
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.Name)
            .Skip(paging.Skip)
            .Take(paging.NormalizedSize)
            .Select(Mapping.ProjectProjection)
            .ToListAsync(ct);

        return Results.Ok(new PagedResult<ProjectDto>(items, paging.NormalizedPage, paging.NormalizedSize, total));
    }

    private static async Task<IResult> GetAsync(Guid id, PlannerDbContext db, ITeamAccess access, CancellationToken ct)
    {
        var teamId = await db.Projects.Where(p => p.Id == id).Select(p => (Guid?)p.TeamId).FirstOrDefaultAsync(ct);
        if (teamId is null)
        {
            return ApiResults.NotFound("That project");
        }

        if (await ApiResults.RequireTeamAsync(access, teamId.Value, TeamPermission.Read, ct) is { } denied)
        {
            return denied;
        }

        var project = await db.Projects.AsNoTracking()
            .Where(p => p.Id == id)
            .Select(Mapping.ProjectProjection)
            .FirstAsync(ct);

        return Results.Ok(project);
    }

    private static async Task<IResult> CreateAsync(
        CreateProjectRequest request,
        PlannerDbContext db,
        ITeamAccess access,
        IActivityLog activity,
        IRealtimeNotifier notifier,
        CancellationToken ct)
    {
        if (await ApiResults.RequireTeamAsync(access, request.TeamId, TeamPermission.Write, ct) is { } denied)
        {
            return denied;
        }

        var validation = new Validation()
            .Required(request.Name, "name")
            .MaxLength(request.Name, 200, "name")
            .MaxLength(request.Summary, 500, "summary");

        if (request.StartDate is { } start && request.TargetDate is { } target && target < start)
        {
            validation.Add("targetDate", "targetDate cannot fall before startDate.");
        }

        if (validation.HasErrors)
        {
            return validation.ToResult();
        }

        if (await db.Projects.AnyAsync(p => p.TeamId == request.TeamId && p.Name == request.Name, ct))
        {
            return ApiResults.Conflict($"This team already has a project named {request.Name}.");
        }

        if (request.LeadUserId is { } lead && !await db.Users.AnyAsync(u => u.Id == lead, ct))
        {
            return ApiResults.BadRequest("The nominated lead does not exist.");
        }

        var maxSort = await db.Projects.Where(p => p.TeamId == request.TeamId).MaxAsync(p => (double?)p.SortOrder, ct);

        var project = new Project
        {
            TeamId = request.TeamId,
            Name = request.Name.Trim(),
            Summary = request.Summary,
            Description = request.Description,
            Status = request.Status,
            Health = request.Health,
            Color = request.Color,
            LeadUserId = request.LeadUserId,
            StartDate = request.StartDate,
            TargetDate = request.TargetDate,
            SortOrder = (maxSort ?? 0) + 1000,
            CompletedAt = request.Status == ProjectStatus.Completed ? DateTimeOffset.UtcNow : null
        };

        db.Projects.Add(project);
        activity.Record(EntityTypes.Project, project.Id, ActivityActions.Created,
            new { name = project.Name }, teamId: project.TeamId, projectId: project.Id);

        await db.SaveChangesAsync(ct);

        var dto = await db.Projects.AsNoTracking().Where(p => p.Id == project.Id)
            .Select(Mapping.ProjectProjection).FirstAsync(ct);

        await notifier.ProjectChanged(ChangeKind.Created, dto);
        return Results.Created($"/api/v1/projects/{project.Id}", dto);
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateProjectRequest request,
        PlannerDbContext db,
        ITeamAccess access,
        IActivityLog activity,
        IRealtimeNotifier notifier,
        CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (project is null)
        {
            return ApiResults.NotFound("That project");
        }

        if (await ApiResults.RequireTeamAsync(access, project.TeamId, TeamPermission.Write, ct) is { } denied)
        {
            return denied;
        }

        if (request.Name.TryGet(out var name))
        {
            var validation = new Validation().Required(name, "name").MaxLength(name, 200, "name");
            if (validation.HasErrors)
            {
                return validation.ToResult();
            }

            if (await db.Projects.AnyAsync(p => p.TeamId == project.TeamId && p.Name == name && p.Id != id, ct))
            {
                return ApiResults.Conflict($"This team already has a project named {name}.");
            }
        }

        if (request.LeadUserId.TryGet(out var leadId) && leadId is { } lead &&
            !await db.Users.AnyAsync(u => u.Id == lead, ct))
        {
            return ApiResults.BadRequest("The nominated lead does not exist.");
        }

        var previousStatus = project.Status;

        project.Name = request.Name.Or(project.Name)!;
        project.Summary = request.Summary.Or(project.Summary);
        project.Description = request.Description.Or(project.Description);
        project.Status = request.Status.Or(project.Status);
        project.Health = request.Health.Or(project.Health);
        project.Color = request.Color.Or(project.Color)!;
        project.LeadUserId = request.LeadUserId.Or(project.LeadUserId);
        project.StartDate = request.StartDate.Or(project.StartDate);
        project.TargetDate = request.TargetDate.Or(project.TargetDate);
        project.SortOrder = request.SortOrder.Or(project.SortOrder);

        if (project.Status != previousStatus)
        {
            project.CompletedAt = project.Status == ProjectStatus.Completed ? DateTimeOffset.UtcNow : null;
            activity.Record(EntityTypes.Project, project.Id, ActivityActions.Updated,
                new { status = new { from = previousStatus.ToString(), to = project.Status.ToString() } },
                teamId: project.TeamId, projectId: project.Id);
        }

        await db.SaveChangesAsync(ct);

        var dto = await db.Projects.AsNoTracking().Where(p => p.Id == id).Select(Mapping.ProjectProjection).FirstAsync(ct);
        await notifier.ProjectChanged(ChangeKind.Updated, dto);
        return Results.Ok(dto);
    }

    private static Task<IResult> ArchiveAsync(
        Guid id, PlannerDbContext db, ITeamAccess access, IActivityLog activity, IRealtimeNotifier notifier,
        CancellationToken ct) => SetArchivedAsync(id, db, access, activity, notifier, DateTimeOffset.UtcNow, ct);

    private static Task<IResult> RestoreAsync(
        Guid id, PlannerDbContext db, ITeamAccess access, IActivityLog activity, IRealtimeNotifier notifier,
        CancellationToken ct) => SetArchivedAsync(id, db, access, activity, notifier, null, ct);

    private static async Task<IResult> SetArchivedAsync(
        Guid id,
        PlannerDbContext db,
        ITeamAccess access,
        IActivityLog activity,
        IRealtimeNotifier notifier,
        DateTimeOffset? archivedAt,
        CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (project is null)
        {
            return ApiResults.NotFound("That project");
        }

        if (await ApiResults.RequireTeamAsync(access, project.TeamId, TeamPermission.Write, ct) is { } denied)
        {
            return denied;
        }

        project.ArchivedAt = archivedAt;
        activity.Record(EntityTypes.Project, project.Id,
            archivedAt is null ? ActivityActions.Restored : ActivityActions.Archived,
            null, teamId: project.TeamId, projectId: project.Id);

        await db.SaveChangesAsync(ct);

        var dto = await db.Projects.AsNoTracking().Where(p => p.Id == id).Select(Mapping.ProjectProjection).FirstAsync(ct);
        await notifier.ProjectChanged(archivedAt is null ? ChangeKind.Restored : ChangeKind.Archived, dto);
        return Results.Ok(dto);
    }

    private static async Task<IResult> DeleteAsync(
        Guid id,
        PlannerDbContext db,
        ITeamAccess access,
        IRealtimeNotifier notifier,
        CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (project is null)
        {
            return ApiResults.NotFound("That project");
        }

        // Deleting is a lead-level act: issues survive it, but milestones and documents do not.
        if (await ApiResults.RequireTeamAsync(access, project.TeamId, TeamPermission.Administer, ct) is { } denied)
        {
            return denied;
        }

        var dto = await db.Projects.AsNoTracking().Where(p => p.Id == id).Select(Mapping.ProjectProjection).FirstAsync(ct);

        db.Projects.Remove(project);
        await db.SaveChangesAsync(ct);

        await notifier.ProjectChanged(ChangeKind.Deleted, dto);
        return Results.NoContent();
    }

    private static async Task<IResult> ListMilestonesAsync(
        Guid id,
        PlannerDbContext db,
        ITeamAccess access,
        CancellationToken ct)
    {
        var teamId = await db.Projects.Where(p => p.Id == id).Select(p => (Guid?)p.TeamId).FirstOrDefaultAsync(ct);
        if (teamId is null)
        {
            return ApiResults.NotFound("That project");
        }

        if (await ApiResults.RequireTeamAsync(access, teamId.Value, TeamPermission.Read, ct) is { } denied)
        {
            return denied;
        }

        var milestones = await db.Milestones.AsNoTracking()
            .Where(m => m.ProjectId == id)
            .OrderBy(m => m.SortOrder)
            .Select(Mapping.MilestoneProjection)
            .ToListAsync(ct);

        return Results.Ok(milestones);
    }

    private static async Task<IResult> CreateMilestoneAsync(
        Guid id,
        CreateMilestoneRequest request,
        PlannerDbContext db,
        ITeamAccess access,
        IActivityLog activity,
        IRealtimeNotifier notifier,
        CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (project is null)
        {
            return ApiResults.NotFound("That project");
        }

        if (await ApiResults.RequireTeamAsync(access, project.TeamId, TeamPermission.Write, ct) is { } denied)
        {
            return denied;
        }

        var validation = new Validation().Required(request.Name, "name").MaxLength(request.Name, 200, "name");
        if (validation.HasErrors)
        {
            return validation.ToResult();
        }

        if (await db.Milestones.AnyAsync(m => m.ProjectId == id && m.Name == request.Name, ct))
        {
            return ApiResults.Conflict($"This project already has a milestone named {request.Name}.");
        }

        var maxSort = await db.Milestones.Where(m => m.ProjectId == id).MaxAsync(m => (double?)m.SortOrder, ct);

        var milestone = new Milestone
        {
            ProjectId = id,
            Name = request.Name.Trim(),
            Description = request.Description,
            TargetDate = request.TargetDate,
            Status = request.Status,
            SortOrder = (maxSort ?? 0) + 1000,
            CompletedAt = request.Status == MilestoneStatus.Completed ? DateTimeOffset.UtcNow : null
        };

        db.Milestones.Add(milestone);
        activity.Record(EntityTypes.Milestone, milestone.Id, ActivityActions.Created,
            new { name = milestone.Name }, teamId: project.TeamId, projectId: id);

        await db.SaveChangesAsync(ct);

        var dto = await db.Milestones.AsNoTracking().Where(m => m.Id == milestone.Id)
            .Select(Mapping.MilestoneProjection).FirstAsync(ct);

        await notifier.MilestoneChanged(ChangeKind.Created, dto, project.TeamId);
        return Results.Created($"/api/v1/milestones/{milestone.Id}", dto);
    }

    private static async Task<IResult> GetMilestoneAsync(
        Guid milestoneId,
        PlannerDbContext db,
        ITeamAccess access,
        CancellationToken ct)
    {
        var teamId = await db.Milestones.Where(m => m.Id == milestoneId)
            .Select(m => (Guid?)m.Project.TeamId).FirstOrDefaultAsync(ct);

        if (teamId is null)
        {
            return ApiResults.NotFound("That milestone");
        }

        if (await ApiResults.RequireTeamAsync(access, teamId.Value, TeamPermission.Read, ct) is { } denied)
        {
            return denied;
        }

        var dto = await db.Milestones.AsNoTracking().Where(m => m.Id == milestoneId)
            .Select(Mapping.MilestoneProjection).FirstAsync(ct);

        return Results.Ok(dto);
    }

    private static async Task<IResult> UpdateMilestoneAsync(
        Guid milestoneId,
        UpdateMilestoneRequest request,
        PlannerDbContext db,
        ITeamAccess access,
        IRealtimeNotifier notifier,
        CancellationToken ct)
    {
        var milestone = await db.Milestones.Include(m => m.Project)
            .FirstOrDefaultAsync(m => m.Id == milestoneId, ct);

        if (milestone is null)
        {
            return ApiResults.NotFound("That milestone");
        }

        if (await ApiResults.RequireTeamAsync(access, milestone.Project.TeamId, TeamPermission.Write, ct) is { } denied)
        {
            return denied;
        }

        var previousStatus = milestone.Status;

        milestone.Name = request.Name.Or(milestone.Name)!;
        milestone.Description = request.Description.Or(milestone.Description);
        milestone.TargetDate = request.TargetDate.Or(milestone.TargetDate);
        milestone.Status = request.Status.Or(milestone.Status);
        milestone.SortOrder = request.SortOrder.Or(milestone.SortOrder);

        if (milestone.Status != previousStatus)
        {
            milestone.CompletedAt = milestone.Status == MilestoneStatus.Completed ? DateTimeOffset.UtcNow : null;
        }

        await db.SaveChangesAsync(ct);

        var dto = await db.Milestones.AsNoTracking().Where(m => m.Id == milestoneId)
            .Select(Mapping.MilestoneProjection).FirstAsync(ct);

        await notifier.MilestoneChanged(ChangeKind.Updated, dto, milestone.Project.TeamId);
        return Results.Ok(dto);
    }

    private static async Task<IResult> DeleteMilestoneAsync(
        Guid milestoneId,
        PlannerDbContext db,
        ITeamAccess access,
        IRealtimeNotifier notifier,
        CancellationToken ct)
    {
        var milestone = await db.Milestones.Include(m => m.Project)
            .FirstOrDefaultAsync(m => m.Id == milestoneId, ct);

        if (milestone is null)
        {
            return ApiResults.NotFound("That milestone");
        }

        if (await ApiResults.RequireTeamAsync(access, milestone.Project.TeamId, TeamPermission.Write, ct) is { } denied)
        {
            return denied;
        }

        var teamId = milestone.Project.TeamId;
        var dto = await db.Milestones.AsNoTracking().Where(m => m.Id == milestoneId)
            .Select(Mapping.MilestoneProjection).FirstAsync(ct);

        // Issues outlive their milestone; they simply fall back to the project.
        db.Milestones.Remove(milestone);
        await db.SaveChangesAsync(ct);

        await notifier.MilestoneChanged(ChangeKind.Deleted, dto, teamId);
        return Results.NoContent();
    }
}
