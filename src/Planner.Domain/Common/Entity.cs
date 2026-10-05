namespace Planner.Domain.Common;

/// <summary>Base type for every persisted aggregate. Ids are UUIDv7 so they sort by creation time,
/// which keeps Postgres B-tree indexes dense instead of scattering writes like UUIDv4 does.</summary>
public abstract class Entity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
