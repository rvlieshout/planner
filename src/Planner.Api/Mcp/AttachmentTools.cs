using System.ComponentModel;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Planner.Api.Endpoints;
using Planner.Contracts.Common;

namespace Planner.Api.Mcp;

/// <summary>Text files on issues: notes, specs, logs, designs in Markdown. Attachments cannot be edited
/// in place, so "replacing" one means uploading the new file and then removing the old, each through the
/// same code as the app's own upload and remove.</summary>
[McpServerToolType]
public sealed class AttachmentTools(McpReader reader, AttachmentCommands attachments)
{
    /// <summary>Enough for a long spec, and a bound on what one call can put in a model's context.</summary>
    private const int MaxReadBytes = 256 * 1024;

    /// <summary>What an assistant may write in one file. Uploads through the app allow 20 MB; text an
    /// assistant writes has no business being that large.</summary>
    private const int MaxWriteBytes = 1024 * 1024;

    [McpServerTool(Name = "read_attachment", Title = "Read an attachment", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The text of a file attached to an issue (Markdown, plain text, JSON, code...). get_issue lists an issue's attachments with their ids. Binary files and links are described, not read.")]
    public async Task<string> ReadAttachmentAsync(
        [Description("The issue key the file is on, e.g. DEV-42.")] string issue,
        [Description("The attachment's id from get_issue, or its file name.")] string attachment,
        CancellationToken ct = default)
    {
        var id = await ResolveAsync(issue, attachment, ct);
        var read = await attachments.ReadTextAsync(id, MaxReadBytes, ct)
                   ?? throw new McpException("That attachment does not exist or you cannot see it.");

        return McpReader.Serialize(new
        {
            id = read.Attachment.Id.ToBase58(),
            read.Attachment.FileName,
            read.Attachment.SizeBytes,
            read.Text,
            truncated = read.Truncated ? true : (bool?)null,
            note = read.Reason ?? (read.Truncated ? $"Only the first {MaxReadBytes / 1024} KB are shown." : null)
        });
    }

    [McpServerTool(Name = "attach_text", Title = "Attach a text file", ReadOnly = false, Destructive = false,
        Idempotent = false, OpenWorld = false)]
    [Description(
        "Attach a text file (Markdown, plain text, JSON...) to an issue, as the user. If a file with the same " +
        "name is already attached, pass replace: true to put the new one in its place (the old one is " +
        "removed); otherwise it is refused, so nothing is overwritten by accident.")]
    public async Task<string> AttachTextAsync(
        [Description("The issue key, e.g. DEV-42.")] string issue,
        [Description("The file name, with an extension that says what it is, e.g. design.md or notes.txt.")] string fileName,
        [Description("The file's content, as text. At most 1 MB.")] string content,
        [Description("Replace an attached file with the same name.")] bool replace = false,
        CancellationToken ct = default)
    {
        var found = await reader.ResolveIssueAsync(issue, ct);
        var name = fileName.Trim();
        var bytes = Encoding.UTF8.GetBytes(content);

        if (bytes.Length > MaxWriteBytes)
        {
            throw new McpException($"The file is {Math.Ceiling(bytes.Length / 1024.0)} KB; files written this way are limited to {MaxWriteBytes / 1024} KB.");
        }

        var sameName = await reader.Db.Attachments.AsNoTracking()
            .Where(a => a.IssueId == found.Id && a.FileName.ToUpper() == name.ToUpper())
            .Select(a => a.Id)
            .ToListAsync(ct);

        if (sameName.Count > 0 && !replace)
        {
            throw new McpException(
                $"{name} is already attached to {issue.ToUpperInvariant()}. Pass replace: true to replace it, or choose another name.");
        }

        await using var stream = new MemoryStream(bytes, writable: false);
        var stored = await attachments.StoreAsync(found.Id, name, stream, bytes.Length, ct);

        if (stored.Value is null)
        {
            throw new McpException(stored.ErrorMessage ?? "The file could not be attached.");
        }

        // The new file is in; now the old one goes. If it cannot (the user may add files but not remove
        // someone else's), both stay, and the answer says so rather than pretending it was replaced.
        var removed = 0;
        string? kept = null;
        foreach (var old in sameName)
        {
            var deletion = await attachments.DeleteAsync(old, ct);
            if (deletion.IsSuccess)
            {
                removed++;
            }
            else
            {
                kept = deletion.ErrorMessage;
            }
        }

        return McpReader.Serialize(new
        {
            id = stored.Value.Id.ToBase58(),
            stored.Value.FileName,
            stored.Value.SizeBytes,
            replaced = removed > 0 ? removed : (int?)null,
            note = kept is null ? null : $"The new file was attached, but the old one could not be removed: {kept}",
            url = reader.WebUrl($"app/issues/{issue.Trim().ToUpperInvariant()}")
        });
    }

    /// <summary>An attachment by id, or by file name on the issue (the newest, when several share it).</summary>
    private async Task<Guid> ResolveAsync(string issue, string attachment, CancellationToken ct)
    {
        var found = await reader.ResolveIssueAsync(issue, ct);
        var onIssue = reader.Db.Attachments.AsNoTracking().Where(a => a.IssueId == found.Id);
        var wanted = attachment.Trim();

        if (Base58.TryParseId(wanted, out var id) && await onIssue.AnyAsync(a => a.Id == id, ct))
        {
            return id;
        }

        var byName = await onIssue.Where(a => a.FileName.ToUpper() == wanted.ToUpper())
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(ct);

        if (byName is { } named)
        {
            return named;
        }

        var names = await onIssue.OrderBy(a => a.FileName).Select(a => a.FileName).ToListAsync(ct);
        throw new McpException(names.Count == 0
            ? $"{issue.ToUpperInvariant()} has no attachments."
            : $"No attachment '{wanted}' on {issue.ToUpperInvariant()}. Its attachments: {string.Join(", ", names)}");
    }
}
