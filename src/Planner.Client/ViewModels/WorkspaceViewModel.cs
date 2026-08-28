using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Planner.Client.Services;
using Planner.Contracts.Issues;
using Planner.Contracts.Realtime;
using Planner.Contracts.Teams;

namespace Planner.Client.ViewModels;

/// <summary>The signed-in view: pick a team, see its board, watch it change live.</summary>
public sealed partial class WorkspaceViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly PlannerApiClient _api;
    private readonly AuthService _auth;
    private readonly RealtimeService _realtime;
    private readonly SettingsStore _settings;
    private readonly ILogger<WorkspaceViewModel> _logger;

    public WorkspaceViewModel(
        PlannerApiClient api,
        AuthService auth,
        RealtimeService realtime,
        SettingsStore settings,
        ILogger<WorkspaceViewModel> logger)
    {
        _api = api;
        _auth = auth;
        _realtime = realtime;
        _settings = settings;
        _logger = logger;

        _realtime.IssueChanged += OnIssueChanged;
        _realtime.ConnectedChanged += connected =>
            Dispatcher.UIThread.Post(() => IsLive = connected);
    }

    public ObservableCollection<TeamDto> Teams { get; } = [];

    public ObservableCollection<BoardColumnViewModel> Columns { get; } = [];

    [ObservableProperty]
    public partial TeamDto? SelectedTeam { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsLive { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    public string UserName => _auth.CurrentUser?.DisplayName ?? string.Empty;

    public string UserRole => _auth.CurrentUser?.Role ?? string.Empty;

    public string ServerUrl => _settings.Current.ServerUrl;

    public string LiveText => IsLive ? "Live" : "Offline";

    /// <summary>Green when the socket is up, grey when the board is only as fresh as the last fetch.</summary>
    public string LiveColor => IsLive ? "#4CB782" : "#95A2B3";

    partial void OnIsLiveChanged(bool value)
    {
        OnPropertyChanged(nameof(LiveText));
        OnPropertyChanged(nameof(LiveColor));
    }

    public async Task InitialiseAsync(CancellationToken ct)
    {
        await LoadTeamsAsync(ct);
        await _realtime.ConnectAsync(_settings.Current.ServerUrl, ct);
    }

    private async Task LoadTeamsAsync(CancellationToken ct)
    {
        IsLoading = true;
        Error = null;

        try
        {
            var teams = await _api.GetTeamsAsync(ct);

            Teams.Clear();
            foreach (var team in teams)
            {
                Teams.Add(team);
            }

            var remembered = _settings.Current.LastTeamId;
            SelectedTeam = Teams.FirstOrDefault(t => t.Id == remembered) ?? Teams.FirstOrDefault();
        }
        catch (Exception ex) when (ex is PlannerApiException or HttpRequestException)
        {
            _logger.LogWarning(ex, "Could not load teams");
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSelectedTeamChanged(TeamDto? value)
    {
        if (value is null)
        {
            Columns.Clear();
            return;
        }

        var settings = _settings.Current;
        settings.LastTeamId = value.Id;
        _settings.Save(settings);

        _ = LoadBoardAsync(value.Id, CancellationToken.None);
    }

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken ct)
    {
        if (SelectedTeam is { } team)
        {
            await LoadBoardAsync(team.Id, ct);
        }
    }

    [RelayCommand]
    private void SignOut() => _auth.SignOut();

    private async Task LoadBoardAsync(Guid teamId, CancellationToken ct)
    {
        IsLoading = true;
        Error = null;

        try
        {
            var states = await _api.GetWorkflowStatesAsync(teamId, ct);
            var board = await _api.GetBoardAsync(teamId, ct);

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

            _logger.LogInformation("Loaded {Issues} issues across {Columns} columns",
                board.Items.Count, Columns.Count);
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

    /// <summary>Applies one realtime change to the board in place, rather than refetching it. Created
    /// and Updated are treated identically — an upsert is idempotent, so a duplicate delivery or a
    /// replay after reconnecting cannot corrupt the view.</summary>
    private void OnIssueChanged(EntityChange<IssueSummary> change) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (SelectedTeam is null || change.TeamId != SelectedTeam.Id)
            {
                return;
            }

            var existing = Columns
                .SelectMany(c => c.Issues.Select(i => (Column: c, Issue: i)))
                .FirstOrDefault(x => x.Issue.Id == change.Id);

            if (change.Kind is ChangeKind.Deleted or ChangeKind.Archived || change.Entity is null)
            {
                if (existing.Column is not null)
                {
                    existing.Column.Issues.Remove(existing.Issue);
                    existing.Column.RaiseCountChanged();
                }

                return;
            }

            var issue = change.Entity;
            var target = Columns.FirstOrDefault(c => c.Id == issue.StateId);

            if (target is null)
            {
                return; // A state this client has not loaded; the next refresh will pick it up.
            }

            if (existing.Column is null)
            {
                Insert(target, new IssueCardViewModel(issue));
                return;
            }

            if (existing.Column.Id == issue.StateId)
            {
                existing.Issue.Update(issue);

                // A rank change has to reposition the card, not just repaint it.
                if (existing.Column.Issues.IndexOf(existing.Issue) is var index && index >= 0)
                {
                    existing.Column.Issues.RemoveAt(index);
                    Insert(existing.Column, existing.Issue);
                }

                return;
            }

            existing.Column.Issues.Remove(existing.Issue);
            existing.Column.RaiseCountChanged();

            existing.Issue.Update(issue);
            Insert(target, existing.Issue);
        });

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

    public async ValueTask DisposeAsync()
    {
        _realtime.IssueChanged -= OnIssueChanged;
        await _realtime.DisconnectAsync();
    }
}
