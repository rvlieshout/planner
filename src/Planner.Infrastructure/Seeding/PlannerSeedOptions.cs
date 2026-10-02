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

    /// <summary>The password of the demo accounts that <see cref="SeedDemoData"/> creates. Left empty,
    /// each gets a random one that is never shown: the accounts are there to be assigned work, and an
    /// administrator can reset one if someone needs to sign in as it.</summary>
    public string DemoPassword { get; set; } = string.Empty;
}
