using System.Text;
using Microsoft.EntityFrameworkCore;
using Planner.Api.Authorization;
using Planner.Api.Common;
using Planner.Api.Realtime;
using Planner.Contracts.Issues;
using Planner.Contracts.Projects;
using Planner.Infrastructure;

namespace Planner.Api.Endpoints;

// The writes the REST endpoints and the MCP tools share, one service per kind of thing. Each is a thin
// wrapper that hands its request-scoped dependencies to the endpoint's own implementation, so there is
// exactly one copy of every permission check, validation rule, audit entry and realtime broadcast.

public sealed class ProjectCommands(
    PlannerDbContext db,
    ITeamAccess access,
    IActivityLog activity,
    IRealtimeNotifier notifier)
{
    public Task<WriteResult<ProjectDto>> UpdateAsync(Guid id, UpdateProjectRequest request, CancellationToken ct) =>
        ProjectEndpoints.ApplyUpdateAsync(id, request, db, access, activity, notifier, ct);
}

public sealed class DocumentCommands(
    PlannerDbContext db,
    ITeamAccess access,
    CurrentUser current,
    IActivityLog activity,
    IRealtimeNotifier notifier)
{
    public Task<WriteResult<DocumentSummary>> CreateAsync(CreateDocumentRequest request, CancellationToken ct) =>
        DocumentEndpoints.ApplyCreateAsync(request, db, access, current, activity, notifier, ct);

    public Task<WriteResult<DocumentSummary>> UpdateAsync(Guid id, UpdateDocumentRequest request, CancellationToken ct) =>
        DocumentEndpoints.ApplyUpdateAsync(id, request, db, access, current, notifier, ct);
}

public sealed class CommentCommands(
    PlannerDbContext db,
    ITeamAccess access,
    CurrentUser current,
    IActivityLog activity,
    IRealtimeNotifier notifier)
{
    public Task<WriteResult<CommentDto>> CreateAsync(Guid issueId, CreateCommentRequest request, CancellationToken ct) =>
        IssueEndpoints.ApplyCreateCommentAsync(issueId, request, db, access, current, activity, notifier, ct);

    /// <summary>Only the author may edit a comment, whatever their role in the team.</summary>
    public Task<WriteResult<CommentDto>> UpdateAsync(Guid commentId, UpdateCommentRequest request, CancellationToken ct) =>
        IssueEndpoints.ApplyUpdateCommentAsync(commentId, request, db, access, current, notifier, ct);
}

/// <summary>What reading an attachment as text found.</summary>
/// <param name="Text">The content, or null when it is not text (or not stored here).</param>
/// <param name="Truncated">Only the first part was returned.</param>
/// <param name="Reason">Why there is no text, when there is none.</param>
public sealed record AttachmentText(AttachmentDto Attachment, string? Text, bool Truncated, string? Reason);

public sealed class AttachmentCommands(
    PlannerDbContext db,
    ITeamAccess access,
    CurrentUser current,
    IActivityLog activity,
    IRealtimeNotifier notifier,
    IConfiguration config,
    IWebHostEnvironment environment,
    ILoggerFactory loggers)
{
    public Task<WriteResult<AttachmentDto>> StoreAsync(
        Guid issueId, string fileName, Stream content, long? declaredLength, CancellationToken ct) =>
        IssueFileEndpoints.StoreAsync(issueId, fileName, content, declaredLength, db, access, current, activity,
            notifier, config, environment, ct);

    public Task<WriteResult<AttachmentDto>> DeleteAsync(Guid attachmentId, CancellationToken ct) =>
        IssueEndpoints.ApplyDeleteAttachmentAsync(attachmentId, db, access, current, notifier, config, environment,
            loggers, ct);

    /// <summary>An uploaded file's content as UTF-8 text, with the same Read permission a download needs.
    /// Returns null when the attachment does not exist or the caller cannot see it. A file that is not
    /// valid UTF-8, or contains NUL bytes, is binary: it is described, never dumped.</summary>
    public async Task<AttachmentText?> ReadTextAsync(Guid attachmentId, int maxBytes, CancellationToken ct)
    {
        var attachment = await db.Attachments.AsNoTracking().Include(a => a.Issue)
            .FirstOrDefaultAsync(a => a.Id == attachmentId, ct);

        if (attachment is null ||
            !await access.HasAsync(attachment.Issue.TeamId, TeamPermission.Read, ct))
        {
            return null;
        }

        var dto = await db.Attachments.AsNoTracking().Where(a => a.Id == attachmentId)
            .Select(Mapping.AttachmentProjection).FirstAsync(ct);

        await using var stream = IssueFileEndpoints.OpenStored(attachment, config, environment);
        if (stream is null)
        {
            return new AttachmentText(dto, null, false,
                attachment.StorageUri.StartsWith("planner-attachment:", StringComparison.Ordinal)
                    ? "The file is missing from storage."
                    : "This attachment is a link to somewhere else, not a stored file.");
        }

        // One byte more than asked for, to know whether there was more.
        var buffer = new byte[maxBytes + 1];
        var length = 0;
        int read;
        while (length < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(length), ct)) > 0)
        {
            length += read;
        }

        var truncated = length > maxBytes;
        var bytes = buffer.AsSpan(0, Math.Min(length, maxBytes));

        // Cutting at the limit can split a multi-byte character; drop the partial tail before decoding.
        if (truncated)
        {
            var cut = bytes.Length;
            while (cut > 0 && cut > bytes.Length - 4 && (bytes[cut - 1] & 0xC0) == 0x80) cut--;
            if (cut > 0 && bytes[cut - 1] >= 0xC0) cut--;
            bytes = bytes[..cut];
        }

        if (bytes.Contains((byte)0))
        {
            return new AttachmentText(dto, null, false, "This is a binary file, not text.");
        }

        try
        {
            var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return new AttachmentText(dto, text.TrimStart('﻿'), truncated, null);
        }
        catch (DecoderFallbackException)
        {
            return new AttachmentText(dto, null, false, "This is a binary file, not UTF-8 text.");
        }
    }
}
