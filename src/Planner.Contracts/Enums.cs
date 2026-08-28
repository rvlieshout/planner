// Lives in Contracts, not Domain: these enum names travel on the wire and the desktop
// client needs them without taking a dependency on EF Core or ASP.NET Identity.
namespace Planner.Contracts.Enums;

/// <summary>Role of a user inside a single team. Org-level roles live in ASP.NET Identity roles.</summary>
public enum TeamRole
{
    Viewer = 0,
    Member = 1,
    Lead = 2
}

/// <summary>Semantic category of a workflow state. Teams may rename/add states, but every state maps
/// onto one of these so cross-team reporting and "is this done?" checks stay possible.</summary>
public enum WorkflowStateType
{
    Backlog = 0,
    Unstarted = 1,
    Started = 2,
    Completed = 3,
    Canceled = 4
}

/// <summary>Linear-style priority ordering: urgent sorts first, "none" sorts last.</summary>
public enum IssuePriority
{
    None = 0,
    Urgent = 1,
    High = 2,
    Medium = 3,
    Low = 4
}

public enum ProjectStatus
{
    Backlog = 0,
    Planned = 1,
    InProgress = 2,
    Paused = 3,
    Completed = 4,
    Canceled = 5
}

public enum ProjectHealth
{
    OnTrack = 0,
    AtRisk = 1,
    OffTrack = 2
}

public enum MilestoneStatus
{
    Upcoming = 0,
    Active = 1,
    Completed = 2
}

/// <summary>Directed relation stored once from the source issue; the inverse is derived on read.</summary>
public enum IssueRelationType
{
    Related = 0,
    Blocks = 1,
    Duplicates = 2
}
