using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Planner.Client.Controls;
using Planner.Contracts.Enums;
using Planner.Contracts.Issues;
using Planner.Contracts.Teams;

namespace Planner.Client.ViewModels;

/// <summary>One issue, as a board card or a list row. Both views bind to the same view model, so an
/// issue looks and behaves the same wherever it appears.</summary>
public sealed partial class IssueCardViewModel(IssueSummary issue) : ViewModelBase
{
    [ObservableProperty]
    public partial IssueSummary Issue { get; set; } = issue;

    public Guid Id => Issue.Id;

    public string Key => Issue.Key;

    public string Title => Issue.Title;

    public double SortOrder => Issue.SortOrder;

    public Guid StateId => Issue.StateId;

    public string StateName => Issue.StateName;

    public string StateColor => Issue.StateColor;

    public Guid? AssigneeId => Issue.Assignee?.Id;

    public string? AssigneeInitials => Issue.Assignee is null ? null : Initials(Issue.Assignee.DisplayName);

    public string? AssigneeName => Issue.Assignee?.DisplayName;

    public bool HasPriority => Issue.Priority != IssuePriority.None;

    public string PriorityLabel => Issue.Priority switch
    {
        IssuePriority.Urgent => "Urgent",
        IssuePriority.High => "High",
        IssuePriority.Medium => "Medium",
        IssuePriority.Low => "Low",
        _ => string.Empty
    };

    /// <summary>Lucide's signal bars for the ordinary levels, a warning triangle for urgent — the shape
    /// carries the meaning, so priority is legible without relying on colour alone.</summary>
    public Geometry? PriorityIcon => Issue.Priority switch
    {
        IssuePriority.Urgent => AppIcons.Urgent,
        IssuePriority.High => AppIcons.PriorityHigh,
        IssuePriority.Medium => AppIcons.PriorityMedium,
        IssuePriority.Low => AppIcons.PriorityLow,
        _ => null
    };

    public string PriorityColor => Issue.Priority switch
    {
        IssuePriority.Urgent => "#EB5757",
        IssuePriority.High => "#F2994A",
        _ => "#95A2B3"
    };

    public IReadOnlyList<LabelDto> Labels => Issue.Labels;

    public bool HasLabels => Issue.Labels.Count > 0;

    public bool HasEstimate => Issue.Estimate is > 0;

    public string EstimateLabel => Issue.Estimate?.ToString() ?? string.Empty;

    /// <summary>Age of the last change, in the two-character form a list column can afford: 4m, 3h,
    /// 2d. The exact timestamp is a tooltip away.</summary>
    public string UpdatedLabel => Age(Issue.UpdatedAt);

    public string UpdatedTooltip => $"Updated {Issue.UpdatedAt.LocalDateTime:g}";

    public void Update(IssueSummary issue)
    {
        Issue = issue;

        foreach (var property in new[]
                 {
                     nameof(Key), nameof(Title), nameof(SortOrder), nameof(StateId), nameof(StateName),
                     nameof(StateColor), nameof(AssigneeId), nameof(AssigneeInitials), nameof(AssigneeName),
                     nameof(HasPriority), nameof(PriorityLabel), nameof(PriorityIcon), nameof(PriorityColor),
                     nameof(Labels), nameof(HasLabels), nameof(HasEstimate), nameof(EstimateLabel),
                     nameof(UpdatedLabel), nameof(UpdatedTooltip)
                 })
        {
            OnPropertyChanged(property);
        }
    }

    private static string Initials(string displayName)
    {
        var parts = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..1].ToUpperInvariant(),
            _ => (parts[0][..1] + parts[^1][..1]).ToUpperInvariant()
        };
    }

    private static string Age(DateTimeOffset when)
    {
        var elapsed = DateTimeOffset.Now - when;

        return elapsed switch
        {
            { TotalMinutes: < 1 } => "now",
            { TotalHours: < 1 } => $"{(int)elapsed.TotalMinutes}m",
            { TotalDays: < 1 } => $"{(int)elapsed.TotalHours}h",
            { TotalDays: < 7 } => $"{(int)elapsed.TotalDays}d",
            { TotalDays: < 365 } => $"{(int)(elapsed.TotalDays / 7)}w",
            _ => $"{(int)(elapsed.TotalDays / 365)}y"
        };
    }
}

/// <summary>One column: a workflow state and the issues sitting in it.</summary>
public sealed partial class BoardColumnViewModel(WorkflowStateDto state) : ViewModelBase
{
    public Guid Id => state.Id;

    public string Name => state.Name;

    public string Color => state.Color;

    public ObservableCollection<IssueCardViewModel> Issues { get; } = [];

    /// <summary>Set while a drag is hovering this column, so it can say it would take the drop.</summary>
    [ObservableProperty]
    public partial bool IsDropTarget { get; set; }

    public int Count => Issues.Count;

    public bool IsEmpty => Issues.Count == 0;

    public void RaiseCountChanged()
    {
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(IsEmpty));
    }
}
