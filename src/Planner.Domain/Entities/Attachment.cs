using Planner.Domain.Common;

namespace Planner.Domain.Entities;

/// <summary>Metadata for a file or external link attached to an issue. The API stores metadata only;
/// bytes live wherever <c>StorageUri</c> points (a share, S3-compatible store, or an external URL).</summary>
public class Attachment : Entity
{
    public Guid IssueId { get; set; }
    public Issue Issue { get; set; } = null!;

    public string FileName { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public long? SizeBytes { get; set; }
    public string StorageUri { get; set; } = string.Empty;

    public Guid UploadedById { get; set; }
    public Identity.AppUser UploadedBy { get; set; } = null!;
}
