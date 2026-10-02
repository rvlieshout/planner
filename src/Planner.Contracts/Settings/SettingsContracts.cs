using Planner.Contracts.Common;

namespace Planner.Contracts.Settings;

/// <param name="TeamStorageBytes">How many bytes of uploaded attachments one team may hold; 0 for no limit.</param>
public sealed record OrganizationSettingsDto(long TeamStorageBytes);

public sealed record UpdateOrganizationSettingsRequest(Optional<long> TeamStorageBytes);

/// <summary>How much of its attachment storage a team has used.</summary>
/// <param name="LimitBytes">Null when the owner has set no limit.</param>
public sealed record TeamStorageDto(Guid TeamId, long UsedBytes, long? LimitBytes);
