using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Planner.Api.Endpoints;
using Planner.Contracts.Common;
using Planner.Contracts.Issues;
using Planner.Domain.Entities;

namespace Planner.Api.Mcp;

/// <summary>The conversation on an issue. Comments are written as the user, through the same code as the
/// app's comment box, and only the user's own comments can be edited: as in the app, being a team lead
/// is no licence to change what someone else said.</summary>
[McpServerToolType]
public sealed class CommentTools(McpReader reader, CommentCommands comments)
{
    /// <summary>How far back an identical comment by the same person counts as the same request made twice.</summary>
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromMinutes(10);

    [McpServerTool(Name = "list_comments", Title = "Read comments", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "The comments on an issue, oldest first, with their ids and which ones are the user's own. Returns the " +
        "latest `limit`; pass `offset` to go further back. get_issue already shows the latest 30.")]
    public async Task<string> ListCommentsAsync(
        [Description("The issue key, e.g. DEV-42.")] string issue,
        [Description("How many comments, 1 to 200.")] int limit = 50,
        [Description("How many of the newest comments to skip, to page back through a long thread.")] int offset = 0,
        CancellationToken ct = default)
    {
        var found = await reader.ResolveIssueAsync(issue, ct);
        var query = reader.Db.Comments.Where(c => c.IssueId == found.Id);
        var total = await query.CountAsync(ct);
        var skip = Math.Max(0, offset);

        var items = await ReadAsync(reader,
            query.OrderByDescending(c => c.CreatedAt).Skip(skip).Take(Math.Clamp(limit, 1, 200)), ct);

        return McpReader.Serialize(new Page<CommentView>(items, total, skip + items.Count < total));
    }

    [McpServerTool(Name = "add_comment", Title = "Comment on an issue", ReadOnly = false, Destructive = false,
        Idempotent = false, OpenWorld = false)]
    [Description(
        "Post a comment on an issue, as the user, in Markdown. Pass replyTo with a comment id from get_issue or " +
        "list_comments to answer that comment. The same text from the same user on the same issue within 10 " +
        "minutes is treated as a retry and returns the existing comment instead, unless allowDuplicate is set.")]
    public async Task<string> AddCommentAsync(
        [Description("The issue key, e.g. DEV-42.")] string issue,
        [Description("The comment, in Markdown.")] string body,
        [Description("The id of a comment on the same issue to reply to.")] string? replyTo = null,
        [Description("Post it even when the same comment was just posted; only for deliberate repeats.")] bool allowDuplicate = false,
        CancellationToken ct = default)
    {
        var found = await reader.ResolveIssueAsync(issue, ct);
        var key = issue.Trim().ToUpperInvariant();
        Guid? parentId = replyTo is null ? null : ParseId(replyTo);

        // Assistants retry after a timeout; the thread should not say the same thing twice.
        if (!allowDuplicate && !string.IsNullOrWhiteSpace(body))
        {
            var since = DateTimeOffset.UtcNow - DuplicateWindow;
            var existing = await reader.Db.Comments.AsNoTracking()
                .Where(c => c.IssueId == found.Id && c.AuthorId == reader.UserId && c.CreatedAt >= since &&
                            c.Body == body && c.ParentCommentId == parentId)
                .OrderByDescending(c => c.CreatedAt)
                .Select(c => (Guid?)c.Id)
                .FirstOrDefaultAsync(ct);

            if (existing is { } id)
            {
                return await ResultAsync(id, key, ct,
                    note: "You posted this comment on this issue a few minutes ago, so it was not posted again. " +
                          "Pass allowDuplicate: true if it should really appear twice.");
            }
        }

        var outcome = await comments.CreateAsync(found.Id, new CreateCommentRequest(body, parentId), ct);

        return outcome.Value is null
            ? throw new McpException(outcome.ErrorMessage ?? "The comment could not be posted.")
            : await ResultAsync(outcome.Value.Id, key, ct);
    }

    [McpServerTool(Name = "update_comment", Title = "Edit your comment", ReadOnly = false, Destructive = false,
        Idempotent = true, OpenWorld = false)]
    [Description(
        "Replace the text of one of the user's own comments; other people's comments cannot be edited. The " +
        "comment is marked as edited. Take the id from get_issue or list_comments (`mine: true`).")]
    public async Task<string> UpdateCommentAsync(
        [Description("The comment's id.")] string comment,
        [Description("The new text, in Markdown. It replaces the old text entirely.")] string body,
        CancellationToken ct = default)
    {
        var id = ParseId(comment);
        var outcome = await comments.UpdateAsync(id, new UpdateCommentRequest(body), ct);

        if (outcome.Value is null)
        {
            throw new McpException(outcome.ErrorMessage ?? "The comment could not be edited.");
        }

        var key = await reader.Db.Issues.AsNoTracking()
            .Where(i => i.Id == outcome.Value.IssueId)
            .Select(i => i.Team.Key + "-" + i.Number)
            .SingleAsync(ct);

        return await ResultAsync(id, key, ct);
    }

    /// <summary>Comments as a model reads them, in the user's time zone and in the order they were written.
    /// Takes the newest-first page to show.</summary>
    internal static async Task<List<CommentView>> ReadAsync(McpReader reader, IQueryable<Comment> page, CancellationToken ct)
    {
        var zone = await reader.TimeZoneAsync(ct);
        var rows = await page.AsNoTracking()
            .Select(c => new { c.Id, Author = c.Author.DisplayName, c.AuthorId, c.CreatedAt, c.EditedAt, c.Body, c.ParentCommentId })
            .ToListAsync(ct);

        return rows
            .OrderBy(c => c.CreatedAt)
            .Select(c => new CommentView(
                c.Id.ToBase58(),
                c.Author,
                TimeZoneInfo.ConvertTime(c.CreatedAt, zone),
                c.Body,
                c.ParentCommentId?.ToBase58(),
                c.AuthorId == reader.UserId ? true : null,
                c.EditedAt is null ? null : true))
            .ToList();
    }

    private async Task<string> ResultAsync(Guid id, string key, CancellationToken ct, string? note = null)
    {
        var view = (await ReadAsync(reader, reader.Db.Comments.Where(c => c.Id == id), ct)).Single();
        return McpReader.Serialize(new { comment = view, issue = key, note, url = reader.WebUrl($"app/issues/{key}") });
    }

    private static Guid ParseId(string text) =>
        Base58.TryParseId(text.Trim(), out var id)
            ? id
            : throw new McpException($"'{text}' is not a comment id. Take it from get_issue or list_comments.");
}
