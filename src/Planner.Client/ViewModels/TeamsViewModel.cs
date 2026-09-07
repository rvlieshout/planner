using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Planner.Client.Services;
using Planner.Contracts.Auth;
using Planner.Contracts.Common;
using Planner.Contracts.Enums;
using Planner.Contracts.Teams;

namespace Planner.Client.ViewModels;

/// <summary>Teams and who is in them: the teams the caller may administer down the left, the selected
/// team's settings and membership on the right.
///
/// Who "the right users" are is the server's answer, not this page's — an organisation owner or
/// administrator administers every team, a team lead administers the team they lead, and a guest
/// administers nothing whatever team role they were given. The page reads that answer off the
/// caller's own profile so it can show a list rather than offer teams whose every write comes back
/// 403; the API remains the authority, and its refusals are shown as they are written.
///
/// The shape is the project page's, for the same reason: a team is not a thing you fill in and
/// dismiss. Its members are each a resource of their own on the server, maintaining them is a session
/// rather than a single answer, and a create flows straight into filling in — the team exists, the
/// caller is its first lead, and the membership section below comes to life.</summary>
public sealed partial class TeamsViewModel : ViewModelBase, IWorkspaceContent, IUnsavedWork
{
    /// <summary>What the server accepts as a key, asked here so a typo is a sentence under the field
    /// rather than a round trip. Kept deliberately identical to the API's own rule.</summary>
    private static readonly Regex KeyPattern = new("^[A-Z][A-Z0-9]{0,7}$", RegexOptions.Compiled);

    private readonly PlannerApiClient _api;
    private readonly ILogger _logger;
    private readonly MeResponse _caller;

    /// <summary>Teams the caller leads, from the memberships that came back with their profile, plus
    /// any team they create in this session — creating one makes you its first lead, and the profile
    /// this page was handed was fetched before that happened.</summary>
    private readonly HashSet<Guid> _led;

    /// <summary>Active accounts, for the picker that adds one to a team. Inactive accounts are left
    /// out: adding one to a team grants access to somebody who cannot sign in.</summary>
    private readonly List<UserSummary> _directory = [];

    /// <summary>The team the editor is open on, or null while it is a new-team form.</summary>
    private TeamDto? _team;

    private bool _creating;

    // What the team looked like when the editor opened, for the diff on save.
    private string _originalName = string.Empty;
    private string? _originalDescription;
    private string _originalColor = ColorSwatchViewModel.Palette[0];
    private bool _originalPrivate;

    public TeamsViewModel(PlannerApiClient api, ILogger logger, MeResponse caller)
    {
        _api = api;
        _logger = logger;
        _caller = caller;

        // A guest is capped at commenting no matter which team role they were given, so a membership
        // that says Lead grants them nothing here either. That is the server's rule, read off the
        // caller's own profile rather than guessed at.
        _led = caller.Role == "guest"
            ? []
            : caller.Teams
                .Where(t => string.Equals(t.Role, nameof(TeamRole.Lead), StringComparison.OrdinalIgnoreCase))
                .Select(t => t.TeamId)
                .ToHashSet();

        foreach (var color in ColorSwatchViewModel.Palette)
        {
            Swatches.Add(new ColorSwatchViewModel(color));
        }

        TeamColor = ColorSwatchViewModel.Palette[0];
        NewMemberRole = TeamRoleOption.For(TeamRole.Member);

        // Rows are watched by virtue of being in the collection rather than by having been added
        // through the right helper, so no path can leave one unwatched and its edits invisible to the
        // navigator's guard.
        Members.CollectionChanged += OnMembersCollectionChanged;
    }

    /// <summary>Asked before an open editor is replaced by another team; true means discard. Set by
    /// the workspace, which is the layer that can put a modal on the screen.</summary>
    public Func<string, Task<bool>>? ConfirmDiscard { get; init; }

    public string Title => "Teams";

    public string? Subtitle => "Team settings and membership";

    public string StatusSummary => IsEditing
        ? $"{Count(Teams.Count, "team")} · {Count(Members.Count, "member")}"
        : Count(Teams.Count, "team");

    public ObservableCollection<TeamListRow> Teams { get; } = [];

    public ObservableCollection<TeamMemberRowViewModel> Members { get; } = [];

    /// <summary>The people who could still be added — the directory less those already in the
    /// team.</summary>
    public ObservableCollection<UserSummary> Candidates { get; } = [];

    public ObservableCollection<ColorSwatchViewModel> Swatches { get; } = [];

    public IReadOnlyList<TeamRoleOption> Roles => TeamRoleOption.All;

    /// <summary>Only an organisation owner or administrator can create a team; the endpoint is theirs
    /// alone. A lead maintains the team they lead and does not get a New Team button that would only
    /// ever come back 403.</summary>
    public bool CanCreateTeams => _caller.Role is "owner" or "admin";

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>A write is in flight. Distinct from <see cref="IsLoading"/>, which is a fetch: the
    /// workspace refuses to navigate away from a write, and lets a fetch go.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool HasEditor { get; set; }

    [ObservableProperty]
    public partial string TeamKey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TeamName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? TeamColor { get; set; }

    [ObservableProperty]
    public partial bool IsPrivate { get; set; }

    [ObservableProperty]
    public partial UserSummary? SelectedCandidate { get; set; }

    [ObservableProperty]
    public partial TeamRoleOption? NewMemberRole { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    /// <summary>Cleared by the next edit, so it says "this is saved" rather than "this was saved at
    /// some point".</summary>
    [ObservableProperty]
    public partial string? Notice { get; set; }

    public bool IsNew => HasEditor && _creating;

    public bool IsEditing => HasEditor && !_creating;

    public bool IsArchived => _team?.ArchivedAt is not null;

    public bool HasTeams => Teams.Count > 0;

    public bool CanArchive => IsEditing && !IsArchived;

    public bool CanRestore => IsEditing && IsArchived;

    public string EditorTitle => _creating ? "New team" : _originalName;

    public string SaveLabel => _creating ? "Create team" : "Save changes";

    public bool HasMembers => Members.Count > 0;

    public bool HasCandidates => Candidates.Count > 0;

    public string RoleDescription => NewMemberRole?.Description ?? string.Empty;

    /// <summary>Everything on this page the server has not been told about: the form itself, any
    /// member row whose role was changed but not saved, and a person picked in the add row but never
    /// added. Computed rather than tracked, so it cannot fall out of step with the fields it
    /// describes.</summary>
    public bool HasUnsavedChanges =>
        HasEditor && (IsFormDirty || Members.Any(m => m.IsDirty) || SelectedCandidate is not null);

    public string UnsavedSummary => _creating
        ? "This team has not been created yet."
        : $"“{_originalName}” has changes that have not been saved.";

    public Avalonia.Media.Geometry? PlusIcon => Controls.AppIcons.Plus;

    public Avalonia.Media.Geometry? CheckIcon => Controls.AppIcons.Check;

    public Avalonia.Media.Geometry? TrashIcon => Controls.AppIcons.Trash;

    /// <summary>Raised whenever a team is created, renamed, recoloured, archived or restored, so the
    /// workspace's team switcher and sidebar can catch up. The page stays where it is: the workspace
    /// refreshes around it rather than navigating.</summary>
    public event Action? TeamsChanged;

    /// <summary>The same comparison the PATCH is built from, asked as a yes or no. For a team that
    /// does not exist yet the originals are the empty form, so an untouched new-team page is clean and
    /// one with a key or a name typed into it is not.</summary>
    private bool IsFormDirty =>
        NameValue != _originalName ||
        Blank(Description) != _originalDescription ||
        ColorValue != _originalColor ||
        IsPrivate != _originalPrivate ||
        (_creating && KeyValue.Length > 0);

    private string NameValue => Trimmed(TeamName);

    /// <summary>Keys are stored upper-case; typing one in lower case is not a different key.</summary>
    private string KeyValue => Trimmed(TeamKey).ToUpperInvariant();

    /// <summary>A blank colour box means "leave it alone", not "no colour": the swatches are how a
    /// team's colour is changed, and the server has no use for an empty string.</summary>
    private string ColorValue => Blank(TeamColor) ?? _originalColor;

    public async Task LoadAsync(CancellationToken ct)
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        Error = null;
        Notice = null;

        try
        {
            var teams = await _api.GetAdministrationTeamsAsync(ct);

            var directory = new List<UserSummary>();
            for (var page = 1; ; page++)
            {
                var result = await _api.GetUsersAsync(page, includeInactive: false, ct);
                directory.AddRange(result.Items);

                if (!result.HasNext)
                {
                    break;
                }
            }

            _directory.Clear();
            _directory.AddRange(directory);

            SetTeams(teams);
            CloseEditor();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is PlannerApiException or HttpRequestException)
        {
            _logger.LogWarning(ex, "Could not load teams for administration");
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Fills the list with the teams this caller may actually administer. Archived teams sort
    /// last rather than disappearing: restoring one is only possible from a list that still shows
    /// it.</summary>
    private void SetTeams(IEnumerable<TeamDto> teams)
    {
        Teams.Clear();

        foreach (var team in teams.Where(CanAdminister)
                     .OrderBy(t => t.ArchivedAt is not null)
                     .ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Teams.Add(new TeamListRow(team));
        }

        OnPropertyChanged(nameof(HasTeams));
        OnPropertyChanged(nameof(StatusSummary));
    }

    private bool CanAdminister(TeamDto team) => CanCreateTeams || _led.Contains(team.Id);

    private async Task<bool> MayReplaceAsync() =>
        !IsBusy && (!HasUnsavedChanges || ConfirmDiscard is null || await ConfirmDiscard(UnsavedSummary));

    [RelayCommand]
    private async Task SelectTeamAsync(TeamListRow? row)
    {
        if (row is null || (IsEditing && row.Id == _team?.Id) || !await MayReplaceAsync())
        {
            return;
        }

        IsLoading = true;
        Error = null;
        Notice = null;

        try
        {
            // The row carries the team the list was built from, so opening one costs a single request:
            // the membership, which is the part this page exists to change.
            OpenEditor(row.Team);
            await LoadMembersAsync(row.Id, CancellationToken.None);
        }
        catch (Exception ex) when (ex is PlannerApiException or HttpRequestException)
        {
            _logger.LogWarning(ex, "Could not load the members of {TeamKey}", row.Key);
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCreateTeams))]
    private async Task NewTeamAsync()
    {
        if (!await MayReplaceAsync())
        {
            return;
        }

        Error = null;
        Notice = null;
        OpenEditor(null);
    }

    private void OpenEditor(TeamDto? team)
    {
        _team = team;
        _creating = team is null;

        TeamKey = team?.Key ?? string.Empty;
        TeamName = team?.Name ?? string.Empty;
        Description = team?.Description ?? string.Empty;
        TeamColor = team?.Color ?? ColorSwatchViewModel.Palette[0];
        IsPrivate = team?.IsPrivate ?? false;

        Remember(team);
        ClearMembers();

        HasEditor = true;
        NotifyEditor();
    }

    private void CloseEditor()
    {
        _team = null;
        _creating = false;
        HasEditor = false;

        ClearMembers();
        NotifyEditor();
    }

    /// <summary>Snapshots the saved state the next diff is measured against.</summary>
    private void Remember(TeamDto? team)
    {
        _originalName = team?.Name ?? string.Empty;
        _originalDescription = team?.Description;
        _originalColor = team?.Color ?? ColorSwatchViewModel.Palette[0];
        _originalPrivate = team?.IsPrivate ?? false;
    }

    private void NotifyEditor()
    {
        foreach (var row in Teams)
        {
            row.IsSelected = IsEditing && row.Id == _team?.Id;
        }

        foreach (var name in new[]
                 {
                     nameof(IsNew), nameof(IsEditing), nameof(IsArchived), nameof(CanArchive),
                     nameof(CanRestore), nameof(EditorTitle), nameof(SaveLabel), nameof(UnsavedSummary),
                     nameof(HasUnsavedChanges), nameof(HasTeams), nameof(StatusSummary)
                 })
        {
            OnPropertyChanged(name);
        }
    }

    private async Task LoadMembersAsync(Guid teamId, CancellationToken ct)
    {
        ClearMembers();

        foreach (var member in await _api.GetTeamMembersAsync(teamId, ct))
        {
            Members.Add(new TeamMemberRowViewModel(_api, _logger, member));
        }

        RefreshCandidates();
        CountMembers();
    }

    /// <summary>Clear() raises a Reset that carries no OldItems, so the rows are let go by hand
    /// first.</summary>
    private void ClearMembers()
    {
        foreach (var row in Members)
        {
            Detach(row);
        }

        Members.Clear();
        RefreshCandidates();
    }

    /// <summary>Everyone in the directory who is not already in this team. Rebuilt rather than patched
    /// — the list is short, and the alternative is keeping a hand-written merge in step with an add, a
    /// removal and a reload at once.</summary>
    private void RefreshCandidates()
    {
        var selected = SelectedCandidate?.Id;
        var members = Members.Select(m => m.UserId).ToHashSet();

        Candidates.Clear();

        foreach (var user in _directory.Where(u => !members.Contains(u.Id))
                     .OrderBy(u => u.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            Candidates.Add(user);
        }

        // Emptying the collection empties the combo box, which writes its now-invalid selection back
        // as null; a candidate who is still a candidate keeps their place in the add row.
        SelectedCandidate = Candidates.FirstOrDefault(u => u.Id == selected);

        OnPropertyChanged(nameof(HasCandidates));
        OnPropertyChanged(nameof(HasMembers));
        OnPropertyChanged(nameof(StatusSummary));
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    [RelayCommand]
    private void PickColor(ColorSwatchViewModel? swatch)
    {
        if (swatch is not null)
        {
            TeamColor = swatch.Value;
        }
    }

    [RelayCommand]
    private async Task SaveAsync(CancellationToken ct)
    {
        if (!HasEditor || IsBusy)
        {
            return;
        }

        Error = null;
        Notice = null;

        var name = NameValue;

        if (name.Length == 0)
        {
            Error = "Give the team a name.";
            return;
        }

        if (name.Length > 120)
        {
            Error = "A team name is at most 120 characters.";
            return;
        }

        if (_creating && !KeyPattern.IsMatch(KeyValue))
        {
            Error = "The key is 1–8 characters, starts with a letter, and uses only A–Z and 0–9.";
            return;
        }

        IsBusy = true;
        var created = false;

        try
        {
            if (_creating)
            {
                var team = await _api.CreateTeamAsync(
                    new CreateTeamRequest(KeyValue, name, Blank(Description), ColorValue, IsPrivate), ct);

                created = true;

                // Creating a team makes the creator its first lead — that is what keeps a team from
                // being unmanageable by its own members, and it is what this page's authority over the
                // team now rests on.
                _led.Add(team.Id);

                Adopt(team);
                await LoadMembersAsync(team.Id, ct);

                _logger.LogInformation("Created team {Key}", team.Key);
            }
            else if (IsFormDirty)
            {
                var team = await _api.UpdateTeamAsync(_team!.Id, BuildPatch(name), ct);
                Adopt(team);

                _logger.LogInformation("Updated team {Key}", team.Key);
            }

            // The membership is on this page, so it is part of saving it. Anyone picked in the add row
            // counts too: a name sitting there when Save is pressed was meant to be added.
            await AddPendingMemberAsync(ct);
            var unsaved = await SaveMembersAsync(ct);

            if (unsaved > 0)
            {
                // Each row already says what went wrong with it; the page only says how many, so that
                // Save never reports success over work that is still sitting there.
                Error = unsaved == 1
                    ? "One membership could not be saved — see the row for why."
                    : $"{unsaved} memberships could not be saved — see the rows for why.";
            }
            else
            {
                Notice = created ? "Team created. You are its first lead." : "Saved.";
            }

            EnforceOwnAuthority();
        }
        catch (PlannerApiException ex)
        {
            // The API's problem detail is written for a person — "A team with key ENG already exists"
            // beats anything this form could invent.
            Error = created
                ? $"The team was created, but what followed was not saved. {ex.Message}"
                : ex.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Could not save the team");
            Error = "Could not reach the server.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The add row's own button, for adding someone without saving the rest of the page. Save
    /// does this too — anyone picked when it is pressed was meant to be added — so this is the shortcut
    /// rather than the only way in.</summary>
    [RelayCommand]
    private async Task AddMemberAsync(CancellationToken ct)
    {
        if (_team is null || IsBusy)
        {
            return;
        }

        if (SelectedCandidate is null)
        {
            Error = "Pick somebody to add.";
            return;
        }

        Error = null;
        Notice = null;
        IsBusy = true;

        try
        {
            await AddPendingMemberAsync(ct);
        }
        catch (PlannerApiException ex)
        {
            Error = ex.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Could not add the member");
            Error = "Could not reach the server.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Adds whoever is sitting in the add row, and nobody if it is empty. Throws: the callers
    /// own the busy state and the error, because one of them is saving the whole page.</summary>
    private async Task AddPendingMemberAsync(CancellationToken ct)
    {
        if (_team is not { } team || SelectedCandidate is not { } candidate)
        {
            return;
        }

        var role = NewMemberRole?.Value ?? TeamRole.Member;
        var member = await _api.AddTeamMemberAsync(team.Id, new AddTeamMemberRequest(candidate.Id, role), ct);

        Members.Add(new TeamMemberRowViewModel(_api, _logger, member));

        SelectedCandidate = null;
        NewMemberRole = TeamRoleOption.For(TeamRole.Member);

        RefreshCandidates();
        CountMembers();

        _logger.LogInformation("Added {Member} to {TeamKey} as {Role}", member.DisplayName, team.Key, role);
    }

    /// <summary>Saves every row whose role differs from the server's, and reports how many would not
    /// go. A row that fails keeps its own error and stays dirty, which is what the page counts.</summary>
    private async Task<int> SaveMembersAsync(CancellationToken ct)
    {
        var unsaved = 0;

        foreach (var row in Members.Where(m => m.IsDirty).ToList())
        {
            await row.SaveAsync(ct);

            if (row.IsDirty)
            {
                unsaved++;
            }
        }

        return unsaved;
    }

    [RelayCommand]
    private Task ArchiveAsync(CancellationToken ct) => SetArchivedAsync(archive: true, ct);

    [RelayCommand]
    private Task RestoreAsync(CancellationToken ct) => SetArchivedAsync(archive: false, ct);

    /// <summary>Archiving is how a team ends here — deleting one would take its projects, issues and
    /// history with it, which is why the API does not offer it and this page does not either.</summary>
    private async Task SetArchivedAsync(bool archive, CancellationToken ct)
    {
        if (_team is not { } team || IsBusy)
        {
            return;
        }

        Error = null;
        Notice = null;
        IsBusy = true;

        try
        {
            var saved = archive
                ? await _api.ArchiveTeamAsync(team.Id, ct)
                : await _api.RestoreTeamAsync(team.Id, ct);

            Adopt(saved);
            Notice = archive
                ? "Team archived. Its work is kept, and restoring it puts it back."
                : "Team restored.";

            _logger.LogInformation("{Action} team {Key}", archive ? "Archived" : "Restored", saved.Key);
        }
        catch (PlannerApiException ex)
        {
            Error = ex.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Could not change the archived state of {TeamKey}", team.Key);
            Error = "Could not reach the server.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Takes on what the server said the team now is: the editor's originals, the row in the
    /// list, and — for a create — the page's shift from a form into that team's page.</summary>
    private void Adopt(TeamDto team)
    {
        var created = _creating;

        _team = team;
        _creating = false;

        TeamKey = team.Key;
        TeamName = team.Name;
        Description = team.Description ?? string.Empty;
        TeamColor = team.Color;
        IsPrivate = team.IsPrivate;

        Remember(team);

        if (Teams.FirstOrDefault(r => r.Id == team.Id) is { } existing)
        {
            existing.Adopt(team);

            // Archiving moves a team to the end of the list, and a rename can move it anywhere.
            Reorder();
        }
        else
        {
            Teams.Add(new TeamListRow(team));
            Reorder();
        }

        CountMembers();
        NotifyEditor();

        if (created)
        {
            OnPropertyChanged(nameof(HasMembers));
        }

        TeamsChanged?.Invoke();
    }

    /// <summary>Puts the list back in order after a rename or an archive, keeping the row objects — and
    /// so the selection — as they are.</summary>
    private void Reorder()
    {
        var ordered = Teams
            .OrderBy(r => r.IsArchived)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        for (var target = 0; target < ordered.Count; target++)
        {
            var current = Teams.IndexOf(ordered[target]);

            if (current != target)
            {
                Teams.Move(current, target);
            }
        }
    }

    /// <summary>The count on the list row comes from the server, which counted before this page added
    /// or removed anybody. The membership is right here, so it is counted from there instead.</summary>
    private void CountMembers()
    {
        if (_team is { } team && Teams.FirstOrDefault(r => r.Id == team.Id) is { } row)
        {
            row.MemberCount = Members.Count;
        }

        OnPropertyChanged(nameof(StatusSummary));
    }

    /// <summary>A lead who demotes or removes themselves has just handed this page's authority over
    /// the team back. The server refuses the next write either way; dropping the team out of the list
    /// is the honest way to say so, rather than leaving an editor open over a team whose every save
    /// now comes back forbidden.</summary>
    private void EnforceOwnAuthority()
    {
        if (CanCreateTeams || _team is not { } team)
        {
            return;
        }

        if (Members.Any(m => m.UserId == _caller.Id && m.Role == TeamRole.Lead))
        {
            return;
        }

        _led.Remove(team.Id);

        if (Teams.FirstOrDefault(r => r.Id == team.Id) is { } row)
        {
            Teams.Remove(row);
        }

        CloseEditor();
        Notice = $"You no longer lead {team.Name}, so it is no longer yours to administer.";
        TeamsChanged?.Invoke();
    }

    private UpdateTeamRequest BuildPatch(string name)
    {
        var description = Blank(Description);
        var color = ColorValue;

        return new UpdateTeamRequest(
            Name: When(name != _originalName, name),
            Description: When(description != _originalDescription, description),
            Color: When(color != _originalColor, color),
            IsPrivate: When(IsPrivate != _originalPrivate, IsPrivate));
    }

    partial void OnTeamColorChanged(string? value)
    {
        foreach (var swatch in Swatches)
        {
            swatch.IsSelected = string.Equals(swatch.Value, value, StringComparison.OrdinalIgnoreCase);
        }
    }

    partial void OnNewMemberRoleChanged(TeamRoleOption? value) => OnPropertyChanged(nameof(RoleDescription));

    /// <summary>The fields Save is responsible for. Touching any of them retires the "Saved." line,
    /// which would otherwise sit under a form that has since been edited and claim something about it
    /// that is no longer true.</summary>
    private static readonly HashSet<string> FormFields =
    [
        nameof(TeamKey), nameof(TeamName), nameof(Description), nameof(TeamColor), nameof(IsPrivate),
        nameof(SelectedCandidate)
    ];

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        // Guarded against re-entry: raising HasUnsavedChanges and clearing Notice both land back here.
        if (e.PropertyName is not { } name || !FormFields.Contains(name))
        {
            return;
        }

        if (Notice is not null)
        {
            Notice = null;
        }

        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    private void OnMembersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var row in e.OldItems?.OfType<TeamMemberRowViewModel>() ?? [])
        {
            Detach(row);
        }

        foreach (var row in e.NewItems?.OfType<TeamMemberRowViewModel>() ?? [])
        {
            row.Removed += OnMemberRemoved;
            row.PropertyChanged += OnMemberRowChanged;
        }

        OnPropertyChanged(nameof(HasMembers));
        OnPropertyChanged(nameof(StatusSummary));
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    private void Detach(TeamMemberRowViewModel row)
    {
        row.Removed -= OnMemberRemoved;
        row.PropertyChanged -= OnMemberRowChanged;
    }

    private void OnMemberRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TeamMemberRowViewModel.IsDirty))
        {
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
    }

    private void OnMemberRemoved(TeamMemberRowViewModel row)
    {
        Members.Remove(row);
        RefreshCandidates();
        CountMembers();
        EnforceOwnAuthority();
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>A text box binding writes null as readily as it writes text — clearing the box does
    /// exactly that — so nothing here calls Trim on one directly.</summary>
    private static string Trimmed(string? value) => value?.Trim() ?? string.Empty;

    private static Optional<T> When<T>(bool changed, T value) => changed ? Optional<T>.From(value) : default;
}
