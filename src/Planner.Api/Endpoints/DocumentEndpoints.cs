using Microsoft.EntityFrameworkCore;
using Planner.Api.Authorization;
using Planner.Api.Common;
using Planner.Api.Realtime;
using Planner.Contracts.Common;
using Planner.Contracts.Projects;
using Planner.Contracts.Realtime;
using Planner.Domain.Entities;
using Planner.Infrastructure;

namespace Planner.Api.Endpoints;

/// <summary>Project documentation: specs, briefs and decision records written in markdown. Kept
/// separate from a project's description field so documents can be listed, searched and archived
/// on their own, and so a document can outlive the project it was written for.</summary>
public static class DocumentEndpoints
{
    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var documents = app.MapGroup("/api/v1/documents").WithTags("Documents");

        documents.MapGet("/", ListAsync).WithSummary("Documents, filtered by team or project");
        documents.MapGet("/{id:b58}", GetAsync).WithSummary("A document with its markdown body");
        documents.MapPost("/", CreateAsync).WithSummary("Create a document");
        documents.MapPatch("/{id:b58}", UpdateAsync).WithSummary("Update a document");
        documents.MapPost("/{id:b58}/archive", ArchiveAsync).WithSummary("Archive a document");
        documents.MapPost("/{id:b58}/restore", RestoreAsync).WithSummary("Restore an archived document");
        documents.MapDelete("/{id:b58}", DeleteAsync).WithSummary("Delete a document permanently");

        app.MapGet("/api/v1/projects/{projectId:b58}/documents", ListByProjectAsync)
            .WithTags("Documents")
            .WithSummary("Documents attached to a project");

        return app;
    }

    private static async Task<IResult> ListAsync(
        PlannerDbContext db,
        ITeamAccess access,
        [AsParameters] PageQuery paging,
        Guid? teamId,
        Guid? projectId,
        string? search,
        bool? includeArchived,
        CancellationToken ct)
    {
        var readable = await access.ReadableTeamIdsAsync(ct);

        var query = db.Documents.AsNoTracking().Where(d => readable.Contains(d.TeamId));

        if (teamId is { } scopedTeam)
        {
            query = query.Where(d => d.TeamId == scopedTeam);
        }

        if (projectId is { } scopedProject)
        {
            query = query.Where(d => d.ProjectId == scopedProject);
        }

        if (includeArchived != true)
        {
            query = query.Where(d => d.ArchivedAt == null);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(d => EF.Functions.ILike(d.Title, pattern) || EF.Functions.ILike(d.Content, pattern));
        }

        var total = await query.LongCountAsync(ct);
        var items = await query
            .OrderByDescending(d => d.UpdatedAt)
            .Skip(paging.Skip)
            .Take(paging.NormalizedSize)
            .Select(Mapping.DocumentSummaryProjection)
            .ToListAsync(ct);

        return Results.Ok(new PagedResult<DocumentSummary>(items, paging.NormalizedPage, paging.NormalizedSize, total));
    }

    private static async Task<IResult> ListByProjectAsync(
        Guid projectId,
        PlannerDbContext db,
        ITeamAccess access,
        CancellationToken ct)
    {
        var teamId = await db.Projects.Where(p => p.Id == projectId)
            .Select(p => (Guid?)p.TeamId).FirstOrDefaultAsync(ct);

        if (teamId is null)
        {
            return ApiResults.NotFound("That project");
        }

        if (await ApiResults.RequireTeamAsync(access, teamId.Value, TeamPermission.Read, ct) is { } denied)
        {
            return denied;
        }

        var items = await db.Documents.AsNoTracking()
            .Where(d => d.ProjectId == projectId && d.ArchivedAt == null)
            .OrderByDescending(d => d.UpdatedAt)
            .Select(Mapping.DocumentSummaryProjection)
            .ToListAsync(ct);

        return Results.Ok(items);
    }

    private static async Task<IResult> GetAsync(Guid id, PlannerDbContext db, ITeamAccess access, CancellationToken ct)
    {
        var teamId = await db.Documents.Where(d => d.Id == id).Select(d => (Guid?)d.TeamId).FirstOrDefaultAsync(ct);
        if (teamId is null)
        {
            return ApiResults.NotFound("That document");
        }

        if (await ApiResults.RequireTeamAsync(access, teamId.Value, TeamPermission.Read, ct) is { } denied)
        {
            return denied;
        }

        var document = await db.Documents.AsNoTracking().Where(d => d.Id == id)
            .Select(Mapping.DocumentProjection).FirstAsync(ct);

        return Results.Ok(document);
    }

    private static async Task<IResult> CreateAsync(
        CreateDocumentRequest request,
        PlannerDbContext db,
        ITeamAccess access,
        CurrentUser current,
        IActivityLog activity,
        IRealtimeNotifier notifier,
        CancellationToken ct)
    {
        if (await ApiResults.RequireTeamAsync(access, request.TeamId, TeamPermission.Write, ct) is { } denied)
        {
            return denied;
        }

        var validation = new Validation().Required(request.Title, "title").MaxLength(request.Title, 300, "title");
        if (validation.HasErrors)
        {
            return validation.ToResult();
        }

        if (request.ProjectId is { } projectId &&
            !await db.Projects.AnyAsync(p => p.Id == projectId && p.TeamId == request.TeamId, ct))
        {
            return ApiResults.BadRequest("That project does not exist in this team.");
        }

        var document = new Document
        {
            TeamId = request.TeamId,
            ProjectId = request.ProjectId,
            Title = request.Title.Trim(),
            Content = request.Content,
            CreatedById = current.Id
        };

        db.Documents.Add(document);
        activity.Record(EntityTypes.Document, document.Id, ActivityActions.Created,
            new { title = document.Title }, teamId: document.TeamId, projectId: document.ProjectId);

        await db.SaveChangesAsync(ct);

        var summary = await db.Documents.AsNoTracking().Where(d => d.Id == document.Id)
            .Select(Mapping.DocumentSummaryProjection).FirstAsync(ct);

        await notifier.DocumentChanged(ChangeKind.Created, summary);
        return Results.Created($"/api/v1/documents/{document.Id.ToBase58()}", summary);
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateDocumentRequest request,
        PlannerDbContext db,
        ITeamAccess access,
        CurrentUser current,
        IRealtimeNotifier notifier,
        CancellationToken ct)
    {
        var document = await db.Documents.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (document is null)
        {
            return ApiResults.NotFound("That document");
        }

        if (await ApiResults.RequireTeamAsync(access, document.TeamId, TeamPermission.Write, ct) is { } denied)
        {
            return denied;
        }

        if (request.ProjectId.TryGet(out var projectId) && projectId is { } target &&
            !await db.Projects.AnyAsync(p => p.Id == target && p.TeamId == document.TeamId, ct))
        {
            return ApiResults.BadRequest("That project does not exist in this team.");
        }

        document.Title = request.Title.Or(document.Title)!;
        document.Content = request.Content.Or(document.Content)!;
        document.ProjectId = request.ProjectId.Or(document.ProjectId);
        document.UpdatedById = current.Id;

        await db.SaveChangesAsync(ct);

        var summary = await db.Documents.AsNoTracking().Where(d => d.Id == id)
            .Select(Mapping.DocumentSummaryProjection).FirstAsync(ct);

        await notifier.DocumentChanged(ChangeKind.Updated, summary);
        return Results.Ok(summary);
    }

    private static Task<IResult> ArchiveAsync(
        Guid id, PlannerDbContext db, ITeamAccess access, IRealtimeNotifier notifier, CancellationToken ct) =>
        SetArchivedAsync(id, db, access, notifier, DateTimeOffset.UtcNow, ct);

    private static Task<IResult> RestoreAsync(
        Guid id, PlannerDbContext db, ITeamAccess access, IRealtimeNotifier notifier, CancellationToken ct) =>
        SetArchivedAsync(id, db, access, notifier, null, ct);

    private static async Task<IResult> SetArchivedAsync(
        Guid id,
        PlannerDbContext db,
        ITeamAccess access,
        IRealtimeNotifier notifier,
        DateTimeOffset? archivedAt,
        CancellationToken ct)
    {
        var document = await db.Documents.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (document is null)
        {
            return ApiResults.NotFound("That document");
        }

        if (await ApiResults.RequireTeamAsync(access, document.TeamId, TeamPermission.Write, ct) is { } denied)
        {
            return denied;
        }

        document.ArchivedAt = archivedAt;
        await db.SaveChangesAsync(ct);

        var summary = await db.Documents.AsNoTracking().Where(d => d.Id == id)
            .Select(Mapping.DocumentSummaryProjection).FirstAsync(ct);

        await notifier.DocumentChanged(archivedAt is null ? ChangeKind.Restored : ChangeKind.Archived, summary);
        return Results.Ok(summary);
    }

    private static async Task<IResult> DeleteAsync(
        Guid id,
        PlannerDbContext db,
        ITeamAccess access,
        IRealtimeNotifier notifier,
        CancellationToken ct)
    {
        var document = await db.Documents.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (document is null)
        {
            return ApiResults.NotFound("That document");
        }

        if (await ApiResults.RequireTeamAsync(access, document.TeamId, TeamPermission.Administer, ct) is { } denied)
        {
            return denied;
        }

        var summary = await db.Documents.AsNoTracking().Where(d => d.Id == id)
            .Select(Mapping.DocumentSummaryProjection).FirstAsync(ct);

        db.Documents.Remove(document);
        await db.SaveChangesAsync(ct);

        await notifier.DocumentChanged(ChangeKind.Deleted, summary);
        return Results.NoContent();
    }
}
