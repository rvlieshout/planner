using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Planner.Client.Services;
using Planner.Contracts.Issues;
using Planner.Contracts.Realtime;

namespace Planner.Client.ViewModels;

/// <summary>A column-per-workflow-state board, for a whole team or for one project within it.</summary>
public sealed partial class BoardViewModel : ViewModelBase, IWorkspaceContent
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

    public ObservableCollection<BoardColumnViewModel> Columns { get; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    public bool IsEmpty => Columns.Count > 0 && Columns.All(c => c.IsEmpty);

    /// <summary>The status bar line for this board.</summary>
    public string StatusSummary
    {
        get
        {
            if (Columns.Count == 0)
            {
                return string.Empty;
            }

            var issues = Columns.Sum(c => c.Issues.Count);
            return $"{issues} {(issues == 1 ? "issue" : "issues")} in {Columns.Count} columns";
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

            Columns.Clear();
            foreach (var state in states.OrderBy(s => s.Position))
            {
                var column = new BoardColumnViewModel(state);

                foreach (var issue in board.Items.Where(i => i.StateId == state.Id).OrderBy(i => i.SortOrder))
                {
                    column.Issues.Add(new IssueCardViewModel(issue));
                }

                column.RaiseCountChanged();
                Columns.Add(column);
            }

            RaiseCounts();
            _logger.LogInformation("Board '{Title}': {Issues} issues in {Columns} columns",
                Title, board.Items.Count, Columns.Count);
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
