using Avalonia.Media;
using Planner.Client.Controls;
using Planner.Contracts.Enums;
using Planner.Contracts.Projects;
using Planner.Contracts.Teams;

namespace Planner.Client.ViewModels;

/// <summary>Initials for an avatar, in one place. Three views draw one.</summary>
public static class Initials
{
    public static string Of(string displayName)
    {
        var parts = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..1].ToUpperInvariant(),
            _ => (parts[0][..1] + parts[^1][..1]).ToUpperInvariant()
        };
    }
}

/// <summary>The options behind the issue form's property pills.
///
/// Every one of them carries its own icon or colour and its own words, because the pill is all the
/// label the field gets: there is no "Priority:" beside it to say what it is. That is also why each
/// has an explicit empty case — "Unassigned", "No project" — rather than an empty box. A pill has to
/// read as a value at rest, and the empty case is a value: it is the only way back to it once
/// something has been picked.</summary>
public sealed record PriorityOption(IssuePriority Value, string Label)
{
    public static readonly IReadOnlyList<PriorityOption> All =
    [
        new(IssuePriority.None, "No priority"),
        new(IssuePriority.Urgent, "Urgent"),
        new(IssuePriority.High, "High"),
        new(IssuePriority.Medium, "Medium"),
        new(IssuePriority.Low, "Low")
    ];

    public static PriorityOption For(IssuePriority priority) => All.First(p => p.Value == priority);

    /// <summary>Lucide's signal bars for the ordinary levels and a warning triangle for urgent — the
    /// same shapes the board cards use, so the two say the same thing the same way.</summary>
    public Geometry? Icon => Value switch
    {
        IssuePriority.Urgent => AppIcons.Urgent,
        IssuePriority.High => AppIcons.PriorityHigh,
        IssuePriority.Medium => AppIcons.PriorityMedium,
        IssuePriority.Low => AppIcons.PriorityLow,
        _ => AppIcons.NoPriority
    };

    public string Color => Value switch
    {
        IssuePriority.Urgent => "#EB5757",
        IssuePriority.High => "#F2994A",
        _ => "#95A2B3"
    };
}

public sealed record AssigneeOption(TeamMemberDto? Member)
{
    public static readonly AssigneeOption Unassigned = new((TeamMemberDto?)null);

    public string Label => Member?.DisplayName ?? "Unassigned";

    public string? Initials => Member is null ? null : ViewModels.Initials.Of(Member.DisplayName);

    public bool HasMember => Member is not null;

    /// <summary>Stands in for the avatar when nobody holds the issue, so the pill keeps its shape
    /// whether or not it has someone in it.</summary>
    public Geometry? Icon => Member is null ? AppIcons.MyIssues : null;
}

public sealed record ProjectOption(ProjectDto? Project)
{
    public static readonly ProjectOption None = new((ProjectDto?)null);

    public string Label => Project?.Name ?? "No project";

    /// <summary>The project's own colour when there is one, and the placeholder grey when there is
    /// not, so the dot is never missing and the row never reflows.</summary>
    public string Color => Project?.Color ?? "#95A2B3";
}

public sealed record MilestoneOption(MilestoneDto? Milestone)
{
    public static readonly MilestoneOption None = new((MilestoneDto?)null);

    public string Label => Milestone?.Name ?? "No milestone";
}

/// <summary>Story points, on the scale every tracker that has them uses.
///
/// A combo rather than a spinner: a spinner showing a bare number needs a label to say what the number
/// means, which is the thing this form is trying to stop doing. Anything the server sends that is not
/// on the scale is added to it on load, so opening an issue estimated by some other means and saving
/// it does not quietly round the estimate away.</summary>
public sealed record EstimateOption(int? Value)
{
    public static readonly int[] Scale = [1, 2, 3, 5, 8, 13, 21];

    public static readonly EstimateOption None = new((int?)null);

    public string Label => Value is { } points ? $"{points} {(points == 1 ? "point" : "points")}" : "No estimate";
}
