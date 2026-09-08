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

/// <summary>A column-per-workflow-state board, for a whole team or for one project within it. The
/// columns are laid out one per lane, except Todo and Backlog, which share one — see
/// <see cref="Lay"/>.</summary>
public sealed partial class BoardViewModel : ViewModelBase, IIssueContent
{
    private readonly PlannerApiClient _api;
    private readonly ILogger _logger;
    private readonly Guid _teamId;
    private readonly Guid? _projectId;

    public BoardViewModel(
        PlannerApiClient api,
        ILogger logger,
        Guid teamId,
        string title,
        string? subtitle = null,
        Guid? projectId = null)
    {
        _api = api;
        _logger = logger;
        _teamId = teamId;
        _projectId = projectId;

        Title = title;
        Subtitle = subtitle;
    }

    public string Title { get; }

    public string? Subtitle { get; }

    /// <summary>What the board is made of, left to right. A lane is one column wide and holds the
    /// column, or columns, drawn in it.</summary>
    public ObservableCollection<BoardLaneViewModel> Lanes { get; } = [];

    /// <summary>Every column on the board, whichever lane it is drawn in. A drop lands in a column, an
    /// issue belongs to a column, and the count in the status bar is a count of columns — none of that
    /// changed when two of them started sharing a lane.</summary>
    public IEnumerable<BoardColumnViewModel> Columns => Lanes.SelectMany(l => l.Columns);

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    public bool IsEmpty => Lanes.Count > 0 && Columns.All(c => c.IsEmpty);

    /// <summary>The status bar line for this board.</summary>
    public string StatusSummary
    {
        get
        {
            if (Lanes.Count == 0)
            {
                return string.Empty;
            }

            var columns = Columns.ToList();
            var issues = columns.Sum(c => c.Issues.Count);
            return $"{issues} {(issues == 1 ? "issue" : "issues")} in {columns.Count} columns";
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


    /// <summary>Drops <paramref name="card"/> into <paramref name="target"/> at <paramref name="index"/>.
    ///
    /// The board is moved first and the server told afterwards: a drag that has to wait for a round
    /// trip before the card lands feels broken. The realtime echo of the same move is an idempotent
    /// upsert, so it only confirms what is already on screen — and a failure puts the board back the
    /// way the server sees it.</summary>
    public async Task MoveAsync(IssueCardViewModel card, BoardColumnViewModel target, int index)
    {
        var source = Columns.FirstOrDefault(c => c.Issues.Contains(card));
        if (source is null)
        {
            return;
        }

        if (ReferenceEquals(source, target))
        {
            var current = source.Issues.IndexOf(card);

            // Dropping below its own slot: the card is about to vacate that row.
            if (index > current)
            {
                index--;
            }

            if (index == current)
            {
                return;
            }
        }

        // The neighbours the card will sit between, with the card itself out of the reckoning.
        var others = target.Issues.Where(i => !ReferenceEquals(i, card)).ToList();
        var after = index > 0 ? others.ElementAtOrDefault(index - 1) : null;
        var before = others.ElementAtOrDefault(index);

        source.Issues.Remove(card);
        target.Issues.Insert(Math.Clamp(index, 0, target.Issues.Count), card);
        source.RaiseCountChanged();
        target.RaiseCountChanged();
        RaiseCounts();

        try
        {
            Error = null;
            var moved = await _api.MoveIssueAsync(card.Id, target.Id, after?.Id, before?.Id, CancellationToken.None);
            card.Update(moved);

            _logger.LogInformation("Moved {Key} to {State}", moved.Key, moved.StateName);
        }
        catch (Exception ex) when (ex is PlannerApiException or HttpRequestException)
        {
            _logger.LogWarning(ex, "Could not move the issue");
            Error = ex.Message;

            await LoadAsync(CancellationToken.None);
        }
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        IsLoading = true;
        Error = null;

        try
        {
            var states = await _api.GetWorkflowStatesAsync(_teamId, ct);

            var board = _projectId is { } project
                ? await _api.GetProjectIssuesAsync(project, ct)
                : await _api.GetBoardAsync(_teamId, ct);

            Lanes.Clear();
            foreach (var lane in Lay(states.OrderBy(s => s.Position).ToList()))
            {
                foreach (var column in lane.Columns)
                {
                    foreach (var issue in board.Items.Where(i => i.StateId == column.Id).OrderBy(i => i.SortOrder))
                    {
                        column.Issues.Add(new IssueCardViewModel(issue));
                    }

                    column.RaiseCountChanged();
                }

                Lanes.Add(lane);
            }

            RaiseCounts();
            _logger.LogInformation("Board '{Title}': {Issues} issues in {Columns} columns across {Lanes} lanes",
                Title, board.Items.Count, Columns.Count(), Lanes.Count);
        }
        catch (Exception ex) when (ex is PlannerApiException or HttpRequestException)
        {
            _logger.LogWarning(ex, "Could not load the board");
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Applies one change in place. Created and Updated are the same upsert, which makes this
    /// idempotent: a duplicate delivery, or the local echo of an issue this client just created, cannot
    /// produce a second card.</summary>
    public void ApplyIssueChange(EntityChange<IssueSummary> change)
    {
        if (change.TeamId != _teamId)
        {
            return;
        }

        var existing = Columns
            .SelectMany(c => c.Issues.Select(i => (Column: c, Issue: i)))
            .FirstOrDefault(x => x.Issue.Id == change.Id);

        var issue = change.Entity;
        var removed = change.Kind is ChangeKind.Deleted or ChangeKind.Archived || issue is null;

        // A project board only shows its own issues, so moving an issue out of the project removes it.
        var belongsHere = !removed && (_projectId is null || issue!.ProjectId == _projectId);

        if (removed || !belongsHere)
        {
            if (existing.Column is not null)
            {
                existing.Column.Issues.Remove(existing.Issue);
                existing.Column.RaiseCountChanged();
                RaiseCounts();
            }

            return;
        }

        var target = Columns.FirstOrDefault(c => c.Id == issue!.StateId);
        if (target is null)
        {
            return; // A state this board has not loaded; the next refresh picks it up.
        }

        if (existing.Column is null)
        {
            Insert(target, new IssueCardViewModel(issue!));
            RaiseCounts();
            return;
        }

        existing.Issue.Update(issue!);

        if (existing.Column.Id == issue!.StateId)
        {
            // Same column: the rank may still have changed, so reposition rather than only repaint.
            var index = existing.Column.Issues.IndexOf(existing.Issue);
            if (index >= 0)
            {
                existing.Column.Issues.RemoveAt(index);
                Insert(existing.Column, existing.Issue);
            }

            return;
        }

        existing.Column.Issues.Remove(existing.Issue);
        existing.Column.RaiseCountChanged();
        Insert(target, existing.Issue);
    }

    /// <summary>Lays the team's workflow states out into lanes: one per state, left to right in the
    /// order the team put them in — with one exception.
    ///
    /// Backlog and the unstarted states share a lane, stacked, unstarted on top. What the board is
    /// asked to do most often is promote something out of the backlog, and that becomes a drag straight
    /// up into the column above rather than a hunt for one somewhere to the right; the arrangement says
    /// where the work is going before the drag starts. The shared lane takes the leftmost of the
    /// positions its states hold, so the rest of the board keeps the order the team gave it.
    ///
    /// The pairing is by <see cref="WorkflowStateType"/> rather than by name, because a team may rename
    /// its states but cannot change what they mean. A team that has only one of the two gets an
    /// ordinary single-column lane out of this, which is exactly what it had before.</summary>
    private static IEnumerable<BoardLaneViewModel> Lay(IReadOnlyList<WorkflowStateDto> ordered)
    {
        var shared = ordered
            .Where(s => s.Type is WorkflowStateType.Unstarted or WorkflowStateType.Backlog)
            .ToList();

        var sharedIds = shared.Select(s => s.Id).ToHashSet();
        var placed = false;

        foreach (var state in ordered)
        {
            if (!sharedIds.Contains(state.Id))
            {
                yield return new BoardLaneViewModel([new BoardColumnViewModel(state)]);
                continue;
            }

            // The lane goes where the first of its states would have gone, and the states after it in
            // the order are drawn inside it rather than beside it.
            if (placed)
            {
                continue;
            }

            placed = true;

            yield return new BoardLaneViewModel(shared
                .OrderBy(s => s.Type == WorkflowStateType.Backlog ? 1 : 0)
                .ThenBy(s => s.Position)
                .Select(s => new BoardColumnViewModel(s)));
        }
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(StatusSummary));
    }

    private static void Insert(BoardColumnViewModel column, IssueCardViewModel card)
    {
        var index = 0;
        while (index < column.Issues.Count && column.Issues[index].SortOrder <= card.SortOrder)
        {
            index++;
        }

        column.Issues.Insert(index, card);
        column.RaiseCountChanged();
    }
}
