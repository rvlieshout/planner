using System.Collections.ObjectModel;
using Avalonia;
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

    /// <summary>Set while this issue is the one being dragged, so the row it came from can step back
    /// and let the card under the cursor be the thing the eye follows.</summary>
    [ObservableProperty]
    public partial bool IsDragging { get; set; }

    public Guid Id => Issue.Id;

    public string Key => Issue.Key;

    public string Title => Issue.Title;

    public double SortOrder => Issue.SortOrder;

    public Guid StateId => Issue.StateId;

    public string StateName => Issue.StateName;

    public string StateColor => Issue.StateColor;

    public Guid? AssigneeId => Issue.Assignee?.Id;

    public string? AssigneeInitials =>
        Issue.Assignee is null ? null : ViewModels.Initials.Of(Issue.Assignee.DisplayName);

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

/// <summary>One lane of the board — one column wide, and usually one column deep.
///
/// The exception is the pair that shares a lane: Todo stacked over Backlog. Promoting work out of the
/// backlog is the move a board is asked for most often, and stacking the two makes it a drag straight
/// up rather than a hunt for a column somewhere to the right. They stay two columns while they do it —
/// their own headers, their own counts, their own drop targets — because they are still two workflow
/// states, and a drop has to land in one of them.
///
/// The lane exists so that a drag can say both things at once: the lane outlines, because it would
/// take the drop, and the column inside it fills, because that is the half it would land in.</summary>
public sealed partial class BoardLaneViewModel : ViewModelBase
{
    public BoardLaneViewModel(IEnumerable<BoardColumnViewModel> columns)
    {
        foreach (var column in columns)
        {
            column.IsFirstInLane = Columns.Count == 0;
            column.PropertyChanged += OnColumnChanged;
            Columns.Add(column);
        }
    }

    public ObservableCollection<BoardColumnViewModel> Columns { get; } = [];

    /// <summary>True while a drag is over any of the columns in this lane. Read rather than set: the
    /// drag machinery lights a column, which is the thing a drop lands in, and the lane follows.</summary>
    public bool IsDropTarget => Columns.Any(c => c.IsDropTarget);

    private void OnColumnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BoardColumnViewModel.IsDropTarget))
        {
            OnPropertyChanged(nameof(IsDropTarget));
        }
    }
}

/// <summary>One column: a workflow state and the issues sitting in it.</summary>
public sealed partial class BoardColumnViewModel(WorkflowStateDto state) : ViewModelBase
{
    public Guid Id => state.Id;

    /// <summary>Whether this is the top column of its lane. The one below draws a rule above its own
    /// header, which is what makes a shared lane read as two stacked columns rather than as one list
    /// that changes its mind halfway down.</summary>
    public bool IsFirstInLane { get; internal set; } = true;

    public string Name => state.Name;

    public string Color => state.Color;

    public ObservableCollection<IssueCardViewModel> Issues { get; } = [];

    /// <summary>Set while a drag is hovering this column, so it can say it would take the drop.</summary>
    [ObservableProperty]
    public partial bool IsDropTarget { get; set; }

    /// <summary>Where the dragged card would land, in pixels down the column's list.
    ///
    /// The column already knows it would take the drop; this is the other half of the answer, and the
    /// half that matters when the drop also decides an order. The view draws a rule at this offset.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DropIndicatorMargin))]
    public partial double DropIndicatorOffset { get; set; }

    /// <summary>The offset as a margin, because a full-width rule pinned to the top of the list is a
    /// Border with a top margin — no canvas, no width binding, no converter.</summary>
    public Thickness DropIndicatorMargin => new(4, DropIndicatorOffset, 4, 0);

    public int Count => Issues.Count;

    public bool IsEmpty => Issues.Count == 0;

    public void RaiseCountChanged()
    {
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(IsEmpty));
    }
}
