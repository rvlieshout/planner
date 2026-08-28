using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Planner.Infrastructure;

/// <summary>Used only by <c>dotnet ef</c>. Keeps migration tooling working without booting the API,
/// so no connection to a live database is required to add a migration.</summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PlannerDbContext>
{
    public PlannerDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__Planner")
            ?? "Host=localhost;Port=5432;Database=planner;Username=planner;Password=planner";

        var options = new DbContextOptionsBuilder<PlannerDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__migrations_history"))
            .UseOpenIddict()
            .Options;

        return new PlannerDbContext(options);
    }
}
