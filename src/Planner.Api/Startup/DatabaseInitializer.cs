using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Planner.Api.Auth;
using Planner.Infrastructure;
using Planner.Infrastructure.Seeding;

namespace Planner.Api.Startup;

public sealed class DatabaseOptions
{
    public const string SectionName = "Planner:Database";

    /// <summary>Applies pending EF migrations on start. Convenient for a single-container on-prem
    /// install; turn it off where a DBA applies migrations out of band.</summary>
    public bool AutoMigrate { get; set; } = true;
}

public static class DatabaseInitializer
{
    public static async Task InitializeDatabaseAsync(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        var auth = app.Services.GetRequiredService<IOptions<PlannerAuthOptions>>().Value;
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Planner.Startup");

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlannerDbContext>();

        if (options.AutoMigrate)
        {
            logger.LogInformation("Applying database migrations");
            await db.Database.MigrateAsync();
        }
        else if (!await db.Database.CanConnectAsync())
        {
            throw new InvalidOperationException("The database is not reachable.");
        }

        await scope.ServiceProvider.GetRequiredService<OpenIddictClientSeeder>().SeedAsync(auth);
        await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedAsync();
    }
}
