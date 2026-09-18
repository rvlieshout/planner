namespace Planner.Infrastructure.Seeding;

public sealed class PlannerSeedOptions
{
    public const string SectionName = "Planner:Seed";

    /// <summary>Bootstrap owner account, created on first start so a fresh install is reachable.</summary>
    public string OwnerEmail { get; set; } = "owner@planner.local";

    public string OwnerPassword { get; set; } = string.Empty;

    public string OwnerDisplayName { get; set; } = "Planner Owner";

    /// <summary>Fills an empty database with a sample team, project, milestones and issues.
    /// Intended for evaluation and for developing a client against real-looking data.</summary>
    public bool SeedDemoData { get; set; }
}
