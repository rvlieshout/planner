using Microsoft.EntityFrameworkCore;
using Planner.Api.Auth;
using Planner.Api.Authorization;
using Planner.Api.Common;
using Planner.Contracts.Settings;
using Planner.Domain.Entities;
using Planner.Infrastructure;

namespace Planner.Api.Endpoints;

/// <summary>Organisation-wide settings. Anyone signed in may read them, since they are limits everyone
/// works within; only the owner changes them.</summary>
public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var settings = app.MapGroup("/api/v1/settings").WithTags("Settings");

        settings.MapGet("/", GetAsync).WithSummary("Organisation-wide settings");

        settings.MapPatch("/", UpdateAsync)
            .RequireAuthorization(PlannerPolicies.OrgOwner)
            .WithSummary("Change organisation-wide settings");

        app.MapGet("/api/v1/teams/{id:b58}/storage", GetTeamStorageAsync)
            .WithTags("Teams")
            .WithSummary("How much of its attachment storage a team has used");

        return app;
    }

    private static async Task<IResult> GetAsync(PlannerDbContext db, CancellationToken ct) =>
        Results.Ok(new OrganizationSettingsDto(await TeamStorage.LimitAsync(db, ct) ?? 0));

    private static async Task<IResult> UpdateAsync(
        UpdateOrganizationSettingsRequest request,
        PlannerDbContext db,
        CurrentUser current,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var settings = await db.OrganizationSettings.FirstOrDefaultAsync(s => s.Id == OrganizationSettings.SingletonId, ct);
        if (settings is null)
        {
            settings = new OrganizationSettings();
            db.OrganizationSettings.Add(settings);
        }

        if (request.TeamStorageBytes.TryGet(out var teamStorageBytes))
        {
            if (teamStorageBytes < 0)
            {
                return new Validation()
                    .Add("teamStorageBytes", "teamStorageBytes must be a number of bytes, or 0 for no limit.")
                    .ToResult();
            }

            settings.TeamStorageBytes = teamStorageBytes;
        }

        settings.UpdatedAt = DateTimeOffset.UtcNow;
        settings.UpdatedById = current.Id;
        await db.SaveChangesAsync(ct);

        loggers.CreateLogger("Planner.Settings").LogInformation(
            "User {ActorId} set the team storage limit to {TeamStorageBytes} bytes", current.Id, settings.TeamStorageBytes);

        return Results.Ok(new OrganizationSettingsDto(settings.TeamStorageBytes));
    }

    private static async Task<IResult> GetTeamStorageAsync(
        Guid id, PlannerDbContext db, TeamAccess access, CancellationToken ct)
    {
        if (await ApiResults.RequireTeamAsync(access, id, TeamPermission.Read, ct) is { } denied)
        {
            return denied;
        }

        // An administrator holds every team, including one that does not exist.
        if (!await db.Teams.AnyAsync(t => t.Id == id, ct))
        {
            return ApiResults.NotFound("That team");
        }

        return Results.Ok(new TeamStorageDto(id, await TeamStorage.UsedAsync(db, id, ct), await TeamStorage.LimitAsync(db, ct)));
    }
}

/// <summary>How much uploaded attachment data a team holds, against what the owner allows it.
///
/// Only bytes this server stores count: a link to a file held elsewhere takes up nothing here. Usage is
/// summed from the attachment rows when it is asked for, so removing a file, an issue or a whole project
/// gives the space back without anything having to keep a counter in step.</summary>
public static class TeamStorage
{
    /// <summary>The limit per team in bytes, or null when the owner has set none.</summary>
    public static async Task<long?> LimitAsync(PlannerDbContext db, CancellationToken ct)
    {
        var configured = await db.OrganizationSettings.AsNoTracking()
            .Where(s => s.Id == OrganizationSettings.SingletonId)
            .Select(s => (long?)s.TeamStorageBytes)
            .FirstOrDefaultAsync(ct);

        var limit = configured ?? OrganizationSettings.DefaultTeamStorageBytes;
        return limit > 0 ? limit : null;
    }

    public static Task<long> UsedAsync(PlannerDbContext db, Guid teamId, CancellationToken ct) =>
        db.Attachments
            .Where(a => a.Issue.TeamId == teamId && a.StorageUri.StartsWith("planner-attachment:"))
            .SumAsync(a => a.SizeBytes ?? 0, ct);

    /// <summary>What the team can still take, or null when there is no limit.</summary>
    public static async Task<long?> RemainingAsync(PlannerDbContext db, Guid teamId, CancellationToken ct) =>
        await LimitAsync(db, ct) is { } limit ? Math.Max(0, limit - await UsedAsync(db, teamId, ct)) : null;
}
