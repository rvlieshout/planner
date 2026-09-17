using Microsoft.EntityFrameworkCore;
using Planner.Api.Authorization;
using Planner.Api.Common;
using Planner.Api.Realtime;
using Planner.Contracts.Realtime;
using Planner.Domain.Entities;
using Planner.Infrastructure;

namespace Planner.Api.Endpoints;

/// <summary>File bytes remain outside the public web root and require the issue's team access.</summary>
public static class IssueFileEndpoints
{
    public const long MaxFileBytes = 20 * 1024 * 1024;

    public static void MapIssueFiles(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/issues/{id:guid}/files", UploadAsync).WithTags("Attachments");
        app.MapGet("/api/v1/attachments/{attachmentId:guid}/content", DownloadAsync).WithTags("Attachments");
    }

    private static string FilePath(Guid id, IConfiguration config, IWebHostEnvironment environment) =>
        Path.GetFullPath(Path.Combine(config["Attachments:Path"] ??
            Path.Combine(environment.ContentRootPath, "App_Data", "attachments"), id.ToString("N")));

    public static void DeleteStoredFile(Attachment attachment, IConfiguration config, IWebHostEnvironment environment)
    {
        // External references are not owned by Planner. Never derive a disk path from their URI.
        if (attachment.StorageUri != $"planner-attachment:{attachment.Id}") return;

        // A missing file is already deleted. Other IO failures must leave the record available for retry.
        try
        {
            File.Delete(FilePath(attachment.Id, config, environment));
        }
        catch (DirectoryNotFoundException)
        {
            // The containing directory being absent also means there are no bytes to purge.
        }
    }

    private static async Task<IResult> UploadAsync(
        Guid id, string fileName, HttpRequest request, PlannerDbContext db, ITeamAccess access,
        CurrentUser current, IActivityLog activity, IRealtimeNotifier notifier,
        IConfiguration config, IWebHostEnvironment environment, CancellationToken ct)
    {
        var issue = await db.Issues.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);
        if (issue is null) return ApiResults.NotFound("That issue");
        if (await ApiResults.RequireTeamAsync(access, issue.TeamId, TeamPermission.Comment, ct) is { } denied)
            return denied;
        var name = Path.GetFileName(fileName.Replace('\\', '/')).Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 300)
            return ApiResults.BadRequest("Choose a file with a name of 300 characters or fewer.");
        if (request.ContentLength > MaxFileBytes)
            return ApiResults.BadRequest("Attachments must be 20 MB or smaller.");

        var attachment = new Attachment
        {
            IssueId = id, FileName = name, ContentType = "application/octet-stream", UploadedById = current.Id
        };
        attachment.StorageUri = $"planner-attachment:{attachment.Id}";
        var path = FilePath(attachment.Id, config, environment);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var committed = false;
        try
        {
            long length = 0;
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await request.Body.ReadAsync(buffer, ct)) > 0)
                {
                    length += read;
                    if (length > MaxFileBytes)
                        return ApiResults.BadRequest("Attachments must be 20 MB or smaller.");
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            attachment.SizeBytes = length;
            db.Attachments.Add(attachment);
            activity.Record(EntityTypes.Attachment, attachment.Id, ActivityActions.AttachmentAdded,
                new { fileName = name }, teamId: issue.TeamId, projectId: issue.ProjectId, issueId: id);
            await db.SaveChangesAsync(ct);
            committed = true;
        }
        finally
        {
            if (!committed && File.Exists(path)) File.Delete(path);
        }

        var dto = await db.Attachments.AsNoTracking().Where(a => a.Id == attachment.Id)
            .Select(Mapping.AttachmentProjection).FirstAsync(ct);
        await notifier.AttachmentChanged(ChangeKind.Created, dto, issue.TeamId);
        return Results.Created($"/api/v1/attachments/{attachment.Id}", dto);
    }

    private static async Task<IResult> DownloadAsync(
        Guid attachmentId, PlannerDbContext db, ITeamAccess access,
        IConfiguration config, IWebHostEnvironment environment, CancellationToken ct)
    {
        var attachment = await db.Attachments.AsNoTracking().Include(a => a.Issue)
            .FirstOrDefaultAsync(a => a.Id == attachmentId, ct);
        if (attachment is null) return ApiResults.NotFound("That attachment");
        if (await ApiResults.RequireTeamAsync(access, attachment.Issue.TeamId, TeamPermission.Read, ct) is { } denied)
            return denied;
        if (attachment.StorageUri != $"planner-attachment:{attachment.Id}")
            return ApiResults.NotFound("That uploaded file");
        var path = FilePath(attachment.Id, config, environment);
        return File.Exists(path)
            ? Results.File(path, "application/octet-stream", attachment.FileName, enableRangeProcessing: true)
            : ApiResults.NotFound("That uploaded file");
    }
}
