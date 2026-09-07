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
using Planner.Contracts.Projects;
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

    /// <summary>Where the sidebar was when the project page opened, so closing it lands somewhere the
    /// user recognises rather than on whatever the shell considers home.</summary>
    private NavItemViewModel? _navBeforeProjectEditor;

    /// <summary>The team the workspace is actually showing. <see cref="SelectedTeam"/> is what the
    /// combo box holds, and the two disagree for as long as it takes to answer a discard prompt.</summary>
    private TeamDto? _shownTeam;

    private bool _restoringTeam;

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

    /// <summary>Asked before a page holding unsaved work is replaced; true means go ahead and discard.
    ///
    /// Set by the view, which is the layer that can put a modal on the screen. Left unset — the XAML
    /// previewer, a test — navigation is never interrupted, which is the right default for a caller
    /// that has no user to ask.</summary>
    public Func<string, Task<bool>>? ConfirmDiscard { get; set; }

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

    public bool CanManageUsers => UserRole is "owner" or "admin";

    public bool IsUsersSelected => Content is UsersViewModel;

    [RelayCommand]
    private async Task ManageUsersAsync()
    {
        if (!CanManageUsers || !await MayDiscardAsync()) return;
        Highlight(null);
        Show(new UsersViewModel(_api, _auth.CurrentUser!) { ConfirmDiscard = ConfirmDiscard });
    }

    public string UserInitials => ViewModels.Initials.Of(UserName);

    public string LiveText => IsLive ? "Live" : "Offline";

    /// <summary>Green when the socket is up, grey when the view is only as fresh as the last fetch.</summary>
    public string LiveColor => IsLive ? "#4CB782" : "#95A2B3";

    public Geometry? PlusIcon => AppIcons.Plus;

    public Geometry? RefreshIcon => AppIcons.Refresh;

    public Geometry? SignOutIcon => AppIcons.SignOut;

    public Geometry? TeamIcon => AppIcons.Team;

    public Geometry? SettingsIcon => AppIcons.Settings;

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

    /// <summary>Whether the thing on screen belongs to a project, and so whether there are project
    /// settings to open. A bindable property rather than the command's own CanExecute, which is a
    /// method and cannot be bound to.</summary>
    public bool IsProjectSelected => SelectedNav?.Kind == NavKind.Project;

    partial void OnSelectedNavChanged(NavItemViewModel? value)
    {
        OnPropertyChanged(nameof(IsProjectSelected));
        EditProjectCommand.NotifyCanExecuteChanged();
    }

    partial void OnContentChanged(ViewModelBase? value)
    {
        OnPropertyChanged(nameof(IsUsersSelected));
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
            // Renaming a project on its own page has to reach the toolbar strip above it.
            case nameof(IWorkspaceContent.Title):
                OnPropertyChanged(nameof(ContentTitle));
                break;
            case nameof(IWorkspaceContent.Subtitle):
                OnPropertyChanged(nameof(ContentSubtitle));
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
        if (!_restoringTeam)
        {
            _ = SwitchTeamAsync(value);
        }
    }

    /// <summary>Switching teams throws the current page away like any other navigation, so it asks
    /// first — and puts the combo box back where it was if the answer is no. The combo box has already
    /// moved by the time this runs; that is what <see cref="_shownTeam"/> is for.</summary>
    private async Task SwitchTeamAsync(TeamDto? value)
    {
        if (!await MayDiscardAsync())
        {
            _restoringTeam = true;
            SelectedTeam = _shownTeam;
            _restoringTeam = false;
            return;
        }

        _shownTeam = value;

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

        await BuildNavigationAsync(value, CancellationToken.None);
    }

    /// <summary>The whole of the guard: does the page on screen hold anything, and if so may it go?
    ///
    /// Every route that replaces the content pane goes through here — the sidebar, the View and Project
    /// menus, the project page's own Close button, switching team, refreshing, and signing out. Two
    /// routes deliberately do not: closing the window, and the update service restarting the app.</summary>
    private async Task<bool> MayDiscardAsync()
    {
        if (Content is UsersViewModel { IsLoading: true }) return false;
        if (Content is not IUnsavedWork { HasUnsavedChanges: true } page || ConfirmDiscard is null)
        {
            return true;
        }

        return await ConfirmDiscard(page.UnsavedSummary);
    }

    private async Task BuildNavigationAsync(TeamDto team, CancellationToken ct)
    {
        // Rebuilt on every team switch rather than filtered: the projects belong to the team, and a
        // stale project in the list is a dead link.
        var previousKind = SelectedNav?.Kind;

        PrimaryNav.Clear();
        PrimaryNav.Add(NavItemViewModel.MyIssues());
        PrimaryNav.Add(NavItemViewModel.Board(team.Name));

        await LoadProjectNavAsync(team, null, ct);

        // First open lands on My Issues: it is the one view that is about the person rather than the
        // team, and it is the question someone opens a tracker to answer. Switching teams otherwise
        // keeps the kind of view they were on — except a project view, which cannot survive the switch
        // because the project belonged to the team they just left.
        Navigate(previousKind switch
        {
            NavKind.Board or NavKind.Project => PrimaryNav[1],
            _ => PrimaryNav[0]
        });
    }

    [RelayCommand]
    private async Task SelectAsync(NavItemViewModel? item)
    {
        if (item is null || SelectedTeam is null || !await MayDiscardAsync())
        {
            return;
        }

        Navigate(item);
    }

    /// <summary>The move itself, past the guard. Separate so the routes that have already asked — a
    /// team switch, closing the project page — do not ask a second time.</summary>
    private void Navigate(NavItemViewModel item)
    {
        if (SelectedTeam is not { } team)
        {
            return;
        }

        Highlight(item);

        IIssueContent content = item.Kind switch
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
        Show(content);
    }

    /// <summary>Lights one navigation row and nothing else. Passing null leaves the sidebar with no
    /// selection, which is what the new-project page wants: it belongs to no row yet.</summary>
    private void Highlight(NavItemViewModel? item)
    {
        foreach (var nav in PrimaryNav.Concat(ProjectNav))
        {
            nav.IsSelected = ReferenceEquals(nav, item);
        }

        SelectedNav = item;
    }

    /// <summary>Puts a page in the content pane and starts it loading.</summary>
    private void Show(IWorkspaceContent content)
    {
        // Cancel whatever the previous view was still fetching: its results are no longer wanted.
        _contentLoad.Cancel();
        _contentLoad.Dispose();
        _contentLoad = new CancellationTokenSource();
        var ct = _contentLoad.Token;

        if (Content is INotifyPropertyChanged previous)
        {
            previous.PropertyChanged -= OnContentPropertyChanged;
        }

        var next = (ViewModelBase)content;
        next.PropertyChanged += OnContentPropertyChanged;

        Content = next;
        _ = content.LoadAsync(ct);
    }

    /// <summary>Fills the PROJECTS section, optionally lighting one of the rows it just built.
    ///
    /// Called on a team switch and again after every project save. Rebuilt rather than patched: the
    /// rows are cheap, and the alternative is keeping a hand-written merge in step with a name change,
    /// a colour change, a new project and a reordering all at once.</summary>
    private async Task LoadProjectNavAsync(TeamDto team, Guid? selectProjectId, CancellationToken ct)
    {
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

        if (selectProjectId is { } projectId &&
            ProjectNav.FirstOrDefault(n => n.ProjectId == projectId) is { } row)
        {
            Highlight(row);
        }
    }

    /// <summary>The sidebar's ＋, Project ▸ New Project, or Ctrl+Shift+N. Opens the project form as a
    /// page: no row owns it yet, so the sidebar shows no selection while it is up.</summary>
    [RelayCommand]
    private async Task NewProjectAsync()
    {
        if (SelectedTeam is not { } team || !await MayDiscardAsync())
        {
            return;
        }

        _navBeforeProjectEditor = SelectedNav;
        Highlight(null);

        ShowProjectEditor(ProjectEditorViewModel.ForCreate(
            _api, _loggerFactory.CreateLogger<ProjectEditorViewModel>(), team.Id, team.Name));
    }

    /// <summary>Opens the selected project's settings. The row stays lit: this page is that project,
    /// seen from a different side.</summary>
    [RelayCommand(CanExecute = nameof(CanEditProject))]
    private async Task EditProjectAsync()
    {
        if (SelectedTeam is not { } team ||
            SelectedNav is not { Kind: NavKind.Project, ProjectId: { } projectId } ||
            !await MayDiscardAsync())
        {
            return;
        }

        _navBeforeProjectEditor = SelectedNav;

        ShowProjectEditor(ProjectEditorViewModel.ForEdit(
            _api, _loggerFactory.CreateLogger<ProjectEditorViewModel>(), team.Id, team.Name, projectId));
    }

    private bool CanEditProject() => SelectedNav?.Kind == NavKind.Project;

    private void ShowProjectEditor(ProjectEditorViewModel editor)
    {
        editor.Saved += project => _ = OnProjectSavedAsync(project);
        editor.Closed += () => _ = CloseProjectEditorAsync(editor);

        Show(editor);
    }

    private Task OnProjectSavedAsync(ProjectDto project) =>
        SelectedTeam is { } team
            ? LoadProjectNavAsync(team, project.Id, CancellationToken.None)
            : Task.CompletedTask;

    private async Task CloseProjectEditorAsync(ProjectEditorViewModel editor)
    {
        if (!ReferenceEquals(Content, editor))
        {
            return;
        }

        // A project that now exists is the obvious place to land — including one created a moment ago,
        // whose board is empty and waiting for its first issue. Routed through the guarded command, so
        // Close asks about unsaved work exactly as the sidebar would.
        var landing = editor.ProjectId is { } projectId
            ? ProjectNav.FirstOrDefault(n => n.ProjectId == projectId)
            : null;

        await SelectAsync(landing ?? _navBeforeProjectEditor ?? PrimaryNav.FirstOrDefault());
    }

    /// <summary>View ▸ My Issues (Ctrl+1). Menu entries address the navigation by kind rather than by
    /// the item instance the sidebar happens to hold.</summary>
    [RelayCommand]
    private async Task ShowMyIssuesAsync()
    {
        if (PrimaryNav.Count > 0)
        {
            await SelectAsync(PrimaryNav[0]);
        }
    }

    [RelayCommand]
    private async Task ShowBoardAsync()
    {
        if (PrimaryNav.Count > 1)
        {
            await SelectAsync(PrimaryNav[1]);
        }
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarVisible = !IsSidebarVisible;

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken ct)
    {
        if (Content is IWorkspaceContent content && await MayDiscardAsync())
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
    private async Task SignOutAsync()
    {
        if (await MayDiscardAsync())
        {
            _auth.SignOut();
        }
    }

    private void OnIssueChanged(EntityChange<IssueSummary> change) =>
        Dispatcher.UIThread.Post(() => ApplyToContent(change));

    private void ApplyToContent(EntityChange<IssueSummary> change)
    {
        if (Content is IIssueContent content)
        {
            content.ApplyIssueChange(change);
        }
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
