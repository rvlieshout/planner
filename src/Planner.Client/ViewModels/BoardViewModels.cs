using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Planner.Contracts.Enums;
using Planner.Contracts.Issues;
using Planner.Contracts.Teams;

namespace Planner.Client.ViewModels;

/// <summary>One card on the board.</summary>
public sealed partial class IssueCardViewModel(IssueSummary issue) : ViewModelBase
{
    [ObservableProperty]
    public partial IssueSummary Issue { get; set; } = issue;

    public Guid Id => Issue.Id;

    public string Key => Issue.Key;

    public string Title => Issue.Title;

    public double SortOrder => Issue.SortOrder;

    public Guid StateId => Issue.StateId;

    public string? AssigneeInitials => Issue.Assignee is null ? null : Initials(Issue.Assignee.DisplayName);

    public string? AssigneeName => Issue.Assignee?.DisplayName;

    public string PriorityLabel => Issue.Priority switch
    {
        IssuePriority.Urgent => "Urgent",
        IssuePriority.High => "High",
        IssuePriority.Medium => "Medium",
        IssuePriority.Low => "Low",
        _ => string.Empty
    };

    public bool HasPriority => Issue.Priority != IssuePriority.None;

    /// <summary>Urgent and High earn a warm colour; the rest stay quiet so the board is scannable.</summary>
    public string PriorityColor => Issue.Priority switch
    {
        IssuePriority.Urgent => "#EB5757",
        IssuePriority.High => "#F2994A",
        _ => "#95A2B3"
    };

    public IReadOnlyList<LabelDto> Labels => Issue.Labels;

    public bool HasLabels => Issue.Labels.Count > 0;

    public void Update(IssueSummary issue)
    {
        Issue = issue;

        OnPropertyChanged(nameof(Key));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(SortOrder));
        OnPropertyChanged(nameof(StateId));
        OnPropertyChanged(nameof(AssigneeInitials));
        OnPropertyChanged(nameof(AssigneeName));
        OnPropertyChanged(nameof(PriorityLabel));
        OnPropertyChanged(nameof(HasPriority));
        OnPropertyChanged(nameof(PriorityColor));
        OnPropertyChanged(nameof(Labels));
        OnPropertyChanged(nameof(HasLabels));
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
}

/// <summary>One column: a workflow state and the issues sitting in it.</summary>
public sealed partial class BoardColumnViewModel(WorkflowStateDto state) : ViewModelBase
{
    public Guid Id => state.Id;

    public string Name => state.Name;

    public string Color => state.Color;

    public ObservableCollection<IssueCardViewModel> Issues { get; } = [];

    public int Count => Issues.Count;

    public void RaiseCountChanged() => OnPropertyChanged(nameof(Count));
}
