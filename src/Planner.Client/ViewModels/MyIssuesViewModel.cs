using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Planner.Client.Services;
using Planner.Contracts.Enums;
using Planner.Contracts.Issues;
using Planner.Contracts.Realtime;
using Planner.Contracts.Teams;

namespace Planner.Client.ViewModels;

/// <summary>Everything assigned to the signed-in user, across every team they can see.
///
/// A flat list rather than a board, because these issues come from different teams whose columns do
/// not line up: "In Review" in one team may not exist in another. Grouped by state type instead, which
/// is the one thing every team's workflow agrees on.</summary>
public sealed partial class MyIssuesViewModel(PlannerApiClient api, ILogger logger, Guid userId)
    : ViewModelBase, IWorkspaceContent
{
    /// <summary>Workflow states per team, so dropping onto a group does not refetch them every time.
    /// A team's states change rarely, and a stale entry costs nothing worse than one rejected move.</summary>
    private readonly Dictionary<Guid, IReadOnlyList<WorkflowStateDto>> _states = [];

    public string Title => "My Issues";

    public string? Subtitle => "Assigned to you across all your teams";

    public ObservableCollection<IssueGroupViewModel> Groups { get; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    public bool IsEmpty => Groups.Count == 0;

    /// <summary>The status bar line for this view.</summary>
    public string StatusSummary
    {
        get
        {
            var count = Groups.Sum(g => g.Issues.Count);
            return count == 0 ? "Nothing assigned" : $"{count} assigned to you";
        }
    }

    public event Action<IssueCardViewModel>? IssueActivated;

    [RelayCommand]
    private void OpenIssue(IssueCardViewModel? card)
    {
        if (card is not null)
        {
            IssueActivated?.Invoke(card);
        }
    }


    /// <summary>Moves an issue into the group it was dropped on.
    ///
    /// The groups are state *types*, not states: these issues come from different teams whose columns
    /// do not line up. So the drop resolves to that issue's own team's first state of the type — the
    /// same "Done" the team's board would move it to.</summary>
    public async Task MoveAsync(IssueCardViewModel card, IssueGroupViewModel target)
    {
        if (card.Issue.StateType == target.StateType)
        {
            return;
        }

        try
        {
            Error = null;

            if (!_states.TryGetValue(card.Issue.TeamId, out var states))
            {
                states = await api.GetWorkflowStatesAsync(card.Issue.TeamId, CancellationToken.None);
                _states[card.Issue.TeamId] = states;
            }

            var state = states
                .Where(s => s.Type == target.StateType)
                .OrderBy(s => s.Position)
                .FirstOrDefault();

            if (state is null)
            {
                Error = $"{card.Key.Split('-')[0]} has no “{target.Title}” state.";
                return;
            }

            var moved = await api.MoveIssueAsync(card.Id, state.Id, null, null, CancellationToken.None);
            card.Update(moved);

            // Regroup from what is already on screen: the row has to leave the group it was dragged
            // out of, and the realtime echo would otherwise be the only thing that moved it.
            Rebuild(Groups.SelectMany(g => g.Issues).Select(c => c.Issue).ToList());

            logger.LogInformation("Moved {Key} to {State}", moved.Key, moved.StateName);
        }
        catch (Exception ex) when (ex is PlannerApiException or HttpRequestException)
        {
            logger.LogWarning(ex, "Could not move the issue");
            Error = ex.Message;
        }
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        IsLoading = true;
        Error = null;

        try
        {
            var issues = await api.GetAssignedIssuesAsync(userId, ct);
            Rebuild(issues.Items);

            logger.LogInformation("My Issues: {Count} assigned", issues.Items.Count);
        }
        catch (Exception ex) when (ex is PlannerApiException or HttpRequestException)
        {
            logger.LogWarning(ex, "Could not load assigned issues");
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void ApplyIssueChange(EntityChange<IssueSummary> change)
    {
        var current = Groups.SelectMany(g => g.Issues).ToList();
        var existing = current.FirstOrDefault(i => i.Id == change.Id);

        var issue = change.Entity;
        var gone = change.Kind is ChangeKind.Deleted or ChangeKind.Archived || issue is null;

        // Reassigning an issue away from this user removes it from the list, which is the whole point
        // of the view: it answers "what is on my plate right now".
        var mine = !gone && issue!.Assignee?.Id == userId;

        if (existing is null && !mine)
        {
            return;
        }

        if (existing is not null)
        {
            current.Remove(existing);
        }

        if (mine)
        {
            var card = existing ?? new IssueCardViewModel(issue!);
            card.Update(issue!);
            current.Add(card);
        }

        Rebuild(current.Select(c => c.Issue).ToList());
    }

    private void Rebuild(IReadOnlyList<IssueSummary> issues)
    {
        var order = new[]
        {
            WorkflowStateType.Started, WorkflowStateType.Unstarted, WorkflowStateType.Backlog,
            WorkflowStateType.Completed, WorkflowStateType.Canceled
        };

        Groups.Clear();

        foreach (var type in order)
        {
            var inGroup = issues
                .Where(i => i.StateType == type)
                .OrderBy(i => i.Priority == IssuePriority.None)
                .ThenBy(i => i.Priority)
                .ThenByDescending(i => i.UpdatedAt)
                .ToList();

            if (inGroup.Count == 0)
            {
                continue;
            }

            var group = new IssueGroupViewModel(Describe(type), type);
            foreach (var issue in inGroup)
            {
                group.Issues.Add(new IssueCardViewModel(issue));
            }

            Groups.Add(group);
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(StatusSummary));
    }

    private static string Describe(WorkflowStateType type) => type switch
    {
        WorkflowStateType.Started => "In progress",
        WorkflowStateType.Unstarted => "Todo",
        WorkflowStateType.Backlog => "Backlog",
        WorkflowStateType.Completed => "Done",
        WorkflowStateType.Canceled => "Canceled",
        _ => type.ToString()
    };
}

public sealed partial class IssueGroupViewModel(string title, WorkflowStateType stateType) : ViewModelBase
{
    public string Title { get; } = title;

    /// <summary>What every team's version of this group has in common, and what a drop onto it means.</summary>
    public WorkflowStateType StateType { get; } = stateType;

    public ObservableCollection<IssueCardViewModel> Issues { get; } = [];

    /// <summary>Set while a drag is hovering this group.</summary>
    [ObservableProperty]
    public partial bool IsDropTarget { get; set; }

    public int Count => Issues.Count;
}
