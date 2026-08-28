using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Planner.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddPlannerPersistence(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddDbContext<PlannerDbContext>(options =>
        {
            options.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsHistoryTable("__migrations_history");
                npgsql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(2), null);
            });

            // Registers OpenIddict's application/authorization/scope/token entities in this model.
            options.UseOpenIddict();
        });

        services.AddScoped<IIssueNumberGenerator, IssueNumberGenerator>();

        return services;
    }
}
