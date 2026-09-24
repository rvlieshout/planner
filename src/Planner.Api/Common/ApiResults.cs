using Planner.Api.Authorization;

namespace Planner.Api.Common;

/// <summary>RFC 9457 problem responses with a consistent shape across every endpoint.</summary>
public static class ApiResults
{
    public static IResult Forbidden(string detail) => Results.Problem(
        title: "Forbidden",
        detail: detail,
        statusCode: StatusCodes.Status403Forbidden);

    public static IResult NotFound(string what) => Results.Problem(
        title: "Not found",
        detail: $"{what} does not exist, or you cannot see it.",
        statusCode: StatusCodes.Status404NotFound);

    public static IResult Conflict(string detail) => Results.Problem(
        title: "Conflict",
        detail: detail,
        statusCode: StatusCodes.Status409Conflict);

    public static IResult BadRequest(string detail) => Results.Problem(
        title: "Bad request",
        detail: detail,
        statusCode: StatusCodes.Status400BadRequest);

    /// <summary>Null for an issue that is not archived, otherwise the refusal to send back.
    ///
    /// An archived issue is a record: it can be read, followed, restored or — by a team lead — deleted,
    /// but nothing on it changes. Not its fields, its place on the board, its conversation, its files or
    /// its relations. Checked after permission, so it never tells a stranger that an issue exists.</summary>
    public static IResult? RejectArchived(DateTimeOffset? archivedAt) => archivedAt is null
        ? null
        : Conflict("This issue is archived. Restore it to change it.");

    /// <summary>Returns null when the caller holds <paramref name="required"/> on the team, otherwise the
    /// response to send back. Deliberately answers 404 for teams the caller cannot see at all, so the
    /// API does not leak the existence of private teams through a 403.</summary>
    public static async Task<IResult?> RequireTeamAsync(
        ITeamAccess access,
        Guid teamId,
        TeamPermission required,
        CancellationToken ct = default)
    {
        var permission = await access.GetPermissionAsync(teamId, ct);

        if (permission >= required)
        {
            return null;
        }

        return permission == TeamPermission.None
            ? NotFound("That team")
            : Forbidden($"This action needs {required} permission on the team; you have {permission}.");
    }
}
