using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Planner.Client.Controls;
using Planner.Client.Services;
using Planner.Contracts.Issues;
using Planner.Contracts.Realtime;
using Planner.Contracts.Teams;

namespace Planner.Client.ViewModels;

/// <summary>The signed-in shell: team switcher and navigation on the left, the selected view on the
/// right, and the new-issue form as an overlay on top of both.</summary>
public sealed partial class WorkspaceViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly PlannerApiClient _api;
    private readonly AuthService _auth;
    private readonly RealtimeService _realtime;
    private readonly SettingsStore _settings;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<WorkspaceViewModel> _logger;

    private CancellationTokenSource _contentLoad = new();

    public WorkspaceViewModel(
        PlannerApiClient api,
        AuthService auth,
        RealtimeService realtime,
        SettingsStore settings,
        ILoggerFactory loggerFactory,
        ILogger<WorkspaceViewModel> logger)
    {
        _api = api;
        _auth = auth;
        _realtime = realtime;
        _settings = settings;
        _loggerFactory = loggerFactory;
        _logger = logger;

        _realtime.IssueChanged += OnIssueChanged;
        _realtime.ConnectedChanged += connected => Dispatcher.UIThread.Post(() => IsLive = connected);
    }

    public ObservableCollection<TeamDto> Teams { get; } = [];

    /// <summary>Views that are not tied to one project: My Issues, and the team board.</summary>
    public ObservableCollection<NavItemViewModel> PrimaryNav { get; } = [];

    public ObservableCollection<NavItemViewModel> ProjectNav { get; } = [];

    [ObservableProperty]
    public partial TeamDto? SelectedTeam { get; set; }

    [ObservableProperty]
    public partial NavItemViewModel? SelectedNav { get; set; }

    [ObservableProperty]
    public partial ViewModelBase? Content { get; set; }

    /// <summary>Non-null while the issue form is open, for either creating or editing.</summary>
    [ObservableProperty]
    public partial IssueEditorViewModel? Editor { get; set; }

    [ObservableProperty]
    public partial bool IsLive { get; set; }

    /// <summary>Collapsible, like the tool panes in any editor. Ctrl+B, or View ▸ Sidebar.</summary>
    [ObservableProperty]
    public partial bool IsSidebarVisible { get; set; } = true;

    [ObservableProperty]
    public partial string? Error { get; set; }

    public string UserName => _auth.CurrentUser?.DisplayName ?? string.Empty;

    public string UserRole => _auth.CurrentUser?.Role ?? string.Empty;

    public string UserInitials => Initials(UserName);

    public bool HasProjects => ProjectNav.Count > 0;

    public string LiveText => IsLive ? "Live" : "Offline";

    /// <summary>Green when the socket is up, grey when the view is only as fresh as the last fetch.</summary>
    public string LiveColor => IsLive ? "#4CB782" : "#95A2B3";

    public Geometry? PlusIcon => AppIcons.Plus;

    public Geometry? RefreshIcon => AppIcons.Refresh;

    public Geometry? SignOutIcon => AppIcons.SignOut;

    public Geometry? TeamIcon => AppIcons.Team;

    public string ContentTitle => (Content as IWorkspaceContent)?.Title ?? string.Empty;

    public string? ContentSubtitle => (Content as IWorkspaceContent)?.Subtitle;

    /// <summary>Forwarded to the window's status bar, which sits outside this view's content and so
    /// cannot bind through <see cref="Content"/> itself.</summary>
    public string ContentStatus => (Content as IWorkspaceContent)?.StatusSummary ?? string.Empty;

    public bool IsContentLoading => (Content as IWorkspaceContent)?.IsLoading ?? false;

    partial void OnIsLiveChanged(bool value)
    {
        OnPropertyChanged(nameof(LiveText));
        OnPropertyChanged(nameof(LiveColor));
    }

    partial void OnContentChanged(ViewModelBase? value)
    {
        OnPropertyChanged(nameof(ContentTitle));
        OnPropertyChanged(nameof(ContentSubtitle));
        OnPropertyChanged(nameof(ContentStatus));
        OnPropertyChanged(nameof(IsContentLoading));
    }

    private void OnContentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IWorkspaceContent.StatusSummary):
                OnPropertyChanged(nameof(ContentStatus));
                break;
            case nameof(IWorkspaceContent.IsLoading):
                OnPropertyChanged(nameof(IsContentLoading));
                break;
        }
    }

    public async Task InitialiseAsync(CancellationToken ct)
    {
        await LoadTeamsAsync(ct);
        await _realtime.ConnectAsync(_settings.Current.ServerUrl, ct);
    }

    private async Task LoadTeamsAsync(CancellationToken ct)
    {
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
    }

    partial void OnSelectedTeamChanged(TeamDto? value)
    {
        if (value is null)
        {
            PrimaryNav.Clear();
            ProjectNav.Clear();
            Content = null;
            return;
        }

        var settings = _settings.Current;
        settings.LastTeamId = value.Id;
        _settings.Save(settings);

        _ = BuildNavigationAsync(value, CancellationToken.None);
    }

    private async Task BuildNavigationAsync(TeamDto team, CancellationToken ct)
    {
        // Rebuilt on every team switch rather than filtered: the projects belong to the team, and a
        // stale project in the list is a dead link.
        var previousKind = SelectedNav?.Kind;

        PrimaryNav.Clear();
        PrimaryNav.Add(NavItemViewModel.MyIssues());
        PrimaryNav.Add(NavItemViewModel.Board(team.Name));

        ProjectNav.Clear();

        try
        {
            var projects = await _api.GetProjectsAsync(team.Id, ct);

            foreach (var project in projects.Items.OrderBy(p => p.SortOrder).ThenBy(p => p.Name))
            {
                ProjectNav.Add(NavItemViewModel.Project(project.Id, project.Name, project.Color));
            }
        }
        catch (Exception ex) when (ex is PlannerApiException or HttpRequestException)
        {
            _logger.LogWarning(ex, "Could not load projects for {TeamKey}", team.Key);
        }

        OnPropertyChanged(nameof(HasProjects));

        // First open lands on My Issues: it is the one view that is about the person rather than the
        // team, and it is the question someone opens a tracker to answer. Switching teams otherwise
        // keeps the kind of view they were on — except a project view, which cannot survive the switch
        // because the project belonged to the team they just left.
        Select(previousKind switch
        {
            NavKind.Board or NavKind.Project => PrimaryNav[1],
            _ => PrimaryNav[0]
        });
    }

    [RelayCommand]
    private void Select(NavItemViewModel? item)
    {
        if (item is null || SelectedTeam is not { } team)
        {
            return;
        }

        foreach (var nav in PrimaryNav.Concat(ProjectNav))
        {
            nav.IsSelected = ReferenceEquals(nav, item);
        }

        SelectedNav = item;

        // Cancel whatever the previous view was still fetching: its results are no longer wanted.
        _contentLoad.Cancel();
        _contentLoad.Dispose();
        _contentLoad = new CancellationTokenSource();
        var ct = _contentLoad.Token;

        IWorkspaceContent content = item.Kind switch
        {
            NavKind.MyIssues => new MyIssuesViewModel(
                _api, _loggerFactory.CreateLogger<MyIssuesViewModel>(), _auth.CurrentUser!.Id),

            NavKind.Project => new BoardViewModel(
                _api, _loggerFactory.CreateLogger<BoardViewModel>(), team.Id,
                item.Title, "Project board", item.ProjectId),

            _ => new BoardViewModel(
                _api, _loggerFactory.CreateLogger<BoardViewModel>(), team.Id,
                team.Name, $"{team.Key} · all issues")
        };

        content.IssueActivated += card => _ = OpenIssueAsync(card);

        if (Content is INotifyPropertyChanged previous)
        {
            previous.PropertyChanged -= OnContentPropertyChanged;
        }

        var next = (ViewModelBase)content;
        next.PropertyChanged += OnContentPropertyChanged;

        Content = next;
        _ = content.LoadAsync(ct);
    }

    /// <summary>View ▸ My Issues (Ctrl+1). Menu entries address the navigation by kind rather than by
    /// the item instance the sidebar happens to hold.</summary>
    [RelayCommand]
    private void ShowMyIssues()
    {
        if (PrimaryNav.Count > 0)
        {
            Select(PrimaryNav[0]);
        }
    }

    [RelayCommand]
    private void ShowBoard()
    {
        if (PrimaryNav.Count > 1)
        {
            Select(PrimaryNav[1]);
        }
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarVisible = !IsSidebarVisible;

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken ct)
    {
        if (Content is IWorkspaceContent content)
        {
            await content.LoadAsync(ct);
        }
    }

    [RelayCommand]
    private Task NewIssueAsync()
    {
        // Creating from inside a project pre-selects it — the common case is adding work to the thing
        // you are already looking at.
        var projectId = SelectedNav?.Kind == NavKind.Project ? SelectedNav.ProjectId : null;

        return OpenEditorAsync(team => IssueEditorViewModel.ForCreate(
            _api, _loggerFactory.CreateLogger<IssueEditorViewModel>(), team.Id, team.Name, projectId));
    }

    /// <summary>Opens an existing issue for editing. Bound to the board cards and the My Issues rows,
    /// so the thing you click is the thing you edit.</summary>
    [RelayCommand]
    private Task OpenIssueAsync(IssueCardViewModel? card) =>
        card is null
            ? Task.CompletedTask
            : OpenEditorAsync(team => IssueEditorViewModel.ForEdit(
                _api, _loggerFactory.CreateLogger<IssueEditorViewModel>(), team.Id, team.Name, card.Id));

    private async Task OpenEditorAsync(Func<TeamDto, IssueEditorViewModel> create)
    {
        if (SelectedTeam is not { } team || Editor is not null)
        {
            return;
        }

        var form = create(team);

        form.Cancelled += () => Editor = null;
        form.Saved += saved =>
        {
            Editor = null;

            // Show the result immediately rather than waiting for the socket to echo it back. The
            // upsert is idempotent, so the echo that follows changes nothing.
            var kind = saved.ArchivedAt is null ? ChangeKind.Updated : ChangeKind.Archived;

            ApplyToContent(new EntityChange<IssueSummary>(
                kind, EntityTypes.Issue, saved.Id, saved.TeamId, saved.ProjectId,
                saved.Id, _auth.CurrentUser?.Id ?? Guid.Empty, DateTimeOffset.Now, saved));
        };

        Editor = form;
        await form.LoadAsync(CancellationToken.None);
    }

    [RelayCommand]
    private void CloseEditor() => Editor = null;

    [RelayCommand]
    private void SignOut() => _auth.SignOut();

    private void OnIssueChanged(EntityChange<IssueSummary> change) =>
        Dispatcher.UIThread.Post(() => ApplyToContent(change));

    private void ApplyToContent(EntityChange<IssueSummary> change)
    {
        if (Content is IWorkspaceContent content)
        {
            content.ApplyIssueChange(change);
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

    public async ValueTask DisposeAsync()
    {
        _realtime.IssueChanged -= OnIssueChanged;

        if (Content is { } content)
        {
            content.PropertyChanged -= OnContentPropertyChanged;
        }

        await _contentLoad.CancelAsync();
        _contentLoad.Dispose();
        await _realtime.DisconnectAsync();
    }
}
