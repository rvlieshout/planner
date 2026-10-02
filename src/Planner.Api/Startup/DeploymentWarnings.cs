using Microsoft.Extensions.Options;
using Npgsql;
using Planner.Api.Auth;
using Planner.Infrastructure.Seeding;

namespace Planner.Api.Startup;

public static class DeploymentWarnings
{
    /// <summary>Says so, loudly and on every start, when an installation outside development is still
    /// running on a value that ships in this repository.
    ///
    /// A warning rather than a refusal to start: the key password cannot simply be changed on an
    /// installation that already has certificates, and an upgrade must not be what takes it down. The
    /// values are published, though, so whoever reads the log should know they are in use.</summary>
    public static void WarnAboutShippedDefaults(this WebApplication app, PlannerAuthOptions auth, string connectionString)
    {
        if (app.Environment.IsDevelopment())
        {
            return;
        }

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Planner.Startup");
        var seed = app.Services.GetRequiredService<IOptions<PlannerSeedOptions>>().Value;

        if (auth.KeyPassword is "planner-dev-keys" or "change-me-in-production" or "change-me-keys")
        {
            logger.LogWarning(
                "Planner:Auth:KeyPassword is a value published in the Planner repository, so the token certificates in {KeyDirectory} " +
                "are protected by a password anyone can look up. Changing it makes the existing certificates unreadable: " +
                "set a new password and delete signing.pfx and encryption.pfx, which signs everyone out once",
                auth.KeyDirectory);
        }

        NpgsqlConnectionStringBuilder database;
        try
        {
            database = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            return;
        }

        if (database.Password is "planner" or "change-me-postgres")
        {
            logger.LogWarning(
                "The database password is a value published in the Planner repository. Change it in PostgreSQL and in ConnectionStrings:Planner");
        }

        if (database.IncludeErrorDetail)
        {
            logger.LogWarning(
                "ConnectionStrings:Planner has Include Error Detail=true, which writes the contents of rows into this log when a statement fails");
        }

        if (seed.OwnerPassword is "change-me-owner-password")
        {
            logger.LogWarning(
                "Planner:Seed:OwnerPassword is the placeholder from .env.example. If the owner account was created with it, change that account's password now");
        }
    }
}
