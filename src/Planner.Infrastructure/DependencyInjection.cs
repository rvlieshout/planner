using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Planner.Infrastructure;

public static class DependencyInjection
{
    /// <param name="configure">Anything the host adds on top — the API's save interceptors, which
    /// publish to SignalR and so cannot live in this project. Runs against the request's scope.</param>
    public static IServiceCollection AddPlannerPersistence(
        this IServiceCollection services,
        string connectionString,
        Action<IServiceProvider, DbContextOptionsBuilder>? configure = null)
    {
        services.AddDbContext<PlannerDbContext>((provider, options) =>
        {
            options.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsHistoryTable("__migrations_history");
                npgsql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(2), null);
            });

            // Registers OpenIddict's application/authorization/scope/token entities in this model.
            options.UseOpenIddict();

            configure?.Invoke(provider, options);
        });

        services.AddScoped<IIssueNumberGenerator, IssueNumberGenerator>();

        return services;
    }
}
