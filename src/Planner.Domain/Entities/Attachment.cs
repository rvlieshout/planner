using Planner.Domain.Common;

namespace Planner.Domain.Entities;

/// <summary>Metadata for an uploaded file or external link. A planner-attachment URI identifies
/// private server storage; HTTP links refer to externally shared files.</summary>
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
