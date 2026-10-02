namespace Planner.Domain.Entities;

/// <summary>What the owner decides for the installation as a whole. One row, created the first time a
/// setting is changed; until then every value is its default.</summary>
public class OrganizationSettings
{
    public const int SingletonId = 1;

    public const long DefaultTeamStorageBytes = 5L * 1024 * 1024 * 1024;

    public int Id { get; set; } = SingletonId;

    /// <summary>How many bytes of uploaded attachments one team may hold. Zero means no limit.</summary>
    public long TeamStorageBytes { get; set; } = DefaultTeamStorageBytes;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Guid? UpdatedById { get; set; }
}
