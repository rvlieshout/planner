using Planner.Domain.Common;

namespace Planner.Domain.Entities;

/// <summary>Threaded discussion on an issue. One level of replies is supported via
/// <see cref="ParentCommentId"/>; deleting a parent cascades to its replies.</summary>
public class Comment : Entity
{
    public Guid IssueId { get; set; }
    public Issue Issue { get; set; } = null!;

    public Guid AuthorId { get; set; }
    public Identity.AppUser Author { get; set; } = null!;

    public string Body { get; set; } = string.Empty;

    public Guid? ParentCommentId { get; set; }
    public Comment? ParentComment { get; set; }
    public ICollection<Comment> Replies { get; set; } = [];

    public DateTimeOffset? EditedAt { get; set; }
}
