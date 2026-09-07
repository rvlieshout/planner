using System.Collections.ObjectModel;
using System.Net.Mail;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Planner.Client.Services;
using Planner.Contracts.Auth;
using Planner.Contracts.Common;
using Planner.Contracts.Enums;
using Planner.Contracts.Teams;

namespace Planner.Client.ViewModels;

public sealed partial class UsersViewModel(PlannerApiClient api, MeResponse caller) : ViewModelBase, IWorkspaceContent, IUnsavedWork
{
    private readonly List<UserSummary> _directory = [];
    private UserDetail? _original;
    private bool _creating;

    public string Title => "Users & access";
    public string Subtitle => "Organisation accounts and team rights";
    public string StatusSummary => $"{Users.Count} of {_directory.Count} users";
    public Func<string, Task<bool>>? ConfirmDiscard { get; init; }
    public ObservableCollection<UserDirectoryRow> Users { get; } = [];
    public ObservableCollection<UserTeamRow> Teams { get; } = [];
    public IReadOnlyList<string> Roles { get; } = caller.Role == "owner"
        ? ["member", "guest", "admin", "owner"] : ["member", "guest", "admin"];

    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool HasEditor { get; set; }
    [ObservableProperty] public partial string Search { get; set; } = "";
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial string? Message { get; set; }
    [ObservableProperty] public partial string DisplayName { get; set; } = "";
    [ObservableProperty] public partial string Email { get; set; } = "";
    [ObservableProperty] public partial string TimeZone { get; set; } = "UTC";
    [ObservableProperty] public partial string Role { get; set; } = "member";
    [ObservableProperty] public partial bool IsActive { get; set; } = true;
    [ObservableProperty] public partial string Password { get; set; } = "";
    [ObservableProperty] public partial string PasswordConfirmation { get; set; } = "";

    public bool IsNew => _creating;
    public bool IsExisting => HasEditor && !_creating;
    public string EditorTitle => _creating ? "New user" : _original?.DisplayName ?? "";
    public bool CanChangeRole => _original?.Role != "owner" || caller.Role == "owner";
    public bool CanChangeActive => !_creating && _original?.Id != caller.Id && _original?.Role != "owner" && Role != "owner";
    public string RoleDescription => Role switch
    {
        "owner" => "Full organisation control, including granting and revoking ownership. Administers every team.",
        "admin" => "Manages users and administers every team, including private teams, without membership.",
        "guest" => "Can read and comment in assigned teams. Team roles cannot grant a guest editing rights.",
        _ => "Access is determined by each team role: Lead manages, Member edits, Viewer reads."
    };
    public bool HasUnsavedChanges => HasEditor && (IsLoading ||
        (_creating ? DisplayName.Length > 0 || Email.Length > 0 || Role != "member" || TimeZone != "UTC" :
            _original is { } u && (DisplayName != u.DisplayName || TimeZone != u.TimeZone || Role != u.Role || IsActive != u.IsActive)) ||
        Password.Length > 0 || PasswordConfirmation.Length > 0 || Teams.Any(t => t.IsDirty));
    public string UnsavedSummary => IsLoading ? "A user operation is still in progress." : "There are unsaved user or team access changes.";

    partial void OnSearchChanged(string value) => Filter();
    partial void OnRoleChanged(string value)
    {
        OnPropertyChanged(nameof(RoleDescription));
        OnPropertyChanged(nameof(CanChangeActive));
        foreach (var team in Teams) team.OrgRole = value;
        if (value == "owner") IsActive = true;
    }

    private void Filter()
    {
        Users.Clear();
        foreach (var user in _directory.Where(u => u.DisplayName.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase) ||
                     u.Email.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase)).OrderBy(u => u.DisplayName))
            Users.Add(new UserDirectoryRow(user) { IsSelected = HasEditor && user.Id == _original?.Id });
        OnPropertyChanged(nameof(StatusSummary));
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        if (IsLoading) return;
        IsLoading = true;
        Error = Message = null;
        try
        {
            var users = new List<UserSummary>();
            for (var page = 1; ; page++)
            {
                var result = await api.GetUsersAsync(page, includeInactive: true, ct);
                users.AddRange(result.Items);
                if (!result.HasNext) break;
            }
            _directory.Clear();
            _directory.AddRange(users);
            Filter();
            HasEditor = false;
            UpdateSelection();
            Password = PasswordConfirmation = "";
            Teams.Clear();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsLoading = false; }
    }

    private async Task<bool> MayReplaceAsync() => !IsLoading &&
        (!HasUnsavedChanges || ConfirmDiscard is null || await ConfirmDiscard(UnsavedSummary));

    [RelayCommand]
    private async Task SelectUserAsync(UserDirectoryRow? user)
    {
        if (HasEditor && user?.Id == _original?.Id) return;
        if (user is null || !await MayReplaceAsync()) return;
        IsLoading = true;
        Error = Message = null;
        try
        {
            var detail = await api.GetUserAsync(user.Id, CancellationToken.None);
            var teams = await ReadTeamsAsync(user.Id);
            SetEditor(detail);
            SetTeams(teams);
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task NewUserAsync()
    {
        if (!await MayReplaceAsync()) return;
        IsLoading = true;
        Error = Message = null;
        try
        {
            var teams = await ReadTeamsAsync(null);
            SetEditor(null);
            SetTeams(teams);
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsLoading = false; }
    }

    private async Task<UserTeamRow[]> ReadTeamsAsync(Guid? userId)
    {
        var teams = await api.GetAdministrationTeamsAsync(CancellationToken.None);
        // Bound concurrency: an installation can have many teams.
        var rows = new List<UserTeamRow>();
        foreach (var team in teams.OrderBy(t => t.Name))
        {
            var member = userId is { } id
                ? (await api.GetTeamMembersAsync(team.Id, CancellationToken.None)).FirstOrDefault(m => m.UserId == id)
                : null;
            rows.Add(new UserTeamRow(team, member?.Role));
        }
        return rows.ToArray();
    }

    private void SetTeams(IEnumerable<UserTeamRow> rows)
    {
        Teams.Clear();
        foreach (var row in rows) { row.OrgRole = Role; Teams.Add(row); }
    }

    private void SetEditor(UserDetail? user)
    {
        _original = user;
        _creating = user is null;
        DisplayName = user?.DisplayName ?? "";
        Email = user?.Email ?? "";
        TimeZone = user?.TimeZone ?? "UTC";
        Role = user?.Role ?? "member";
        IsActive = user?.IsActive ?? true;
        Password = PasswordConfirmation = "";
        HasEditor = true;
        NotifyEditor();
    }

    private void NotifyEditor()
    {
        UpdateSelection();
        foreach (var name in new[] { nameof(IsNew), nameof(IsExisting), nameof(EditorTitle), nameof(CanChangeRole), nameof(CanChangeActive) })
            OnPropertyChanged(name);
    }

    private void UpdateSelection()
    {
        foreach (var row in Users)
            row.IsSelected = HasEditor && row.Id == _original?.Id;
    }

    private bool ValidPassword()
    {
        if (Password.Length < 12) { Error = "Use a password of at least 12 characters."; return false; }
        if (Password != PasswordConfirmation) { Error = "The passwords do not match."; return false; }
        return true;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsLoading || !HasEditor) return;
        Error = Message = null;
        if (string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Trim().Length > 150)
        { Error = "Enter a display name of 1–150 characters."; return; }
        if (string.IsNullOrWhiteSpace(TimeZone)) { Error = "Enter a time zone."; return; }
        if (_creating && (!MailAddress.TryCreate(Email.Trim(), out _) || !ValidPassword()))
        { Error ??= "Enter a valid email address."; return; }
        IsLoading = true;
        var saved = false;
        try
        {
            UserSummary summary;
            if (_creating)
            {
                summary = await api.CreateUserAsync(new(Email.Trim(), Password, DisplayName.Trim(), Role, TimeZone.Trim()), CancellationToken.None);
                // Adopt the created ID immediately, so retrying a failed membership never creates a duplicate account.
                _original = new(summary.Id, summary.Email, summary.DisplayName, summary.AvatarUrl, TimeZone.Trim(), Role, true, DateTimeOffset.UtcNow, null);
                _creating = false;
                Password = PasswordConfirmation = "";
            }
            else
            {
                summary = await api.UpdateUserAsync(_original!.Id,
                    new(DisplayName.Trim() != _original.DisplayName ? Optional<string>.From(DisplayName.Trim()) : default,
                        default,
                        TimeZone.Trim() != _original.TimeZone ? Optional<string>.From(TimeZone.Trim()) : default,
                        IsActive != _original.IsActive ? Optional<bool>.From(IsActive) : default,
                        Role != _original.Role ? Optional<string>.From(Role) : default), CancellationToken.None);
                _original = _original with { DisplayName = summary.DisplayName, TimeZone = TimeZone.Trim(), IsActive = summary.IsActive, Role = Role };
            }
            saved = true;
            DisplayName = summary.DisplayName;
            Email = summary.Email;
            TimeZone = _original.TimeZone;
            _directory.RemoveAll(u => u.Id == summary.Id);
            _directory.Add(summary);
            Filter();
            NotifyEditor();
            foreach (var team in Teams.Where(t => t.IsDirty))
            {
                if (team.Selection == "Not a member")
                    await api.RemoveTeamMemberAsync(team.Id, summary.Id, CancellationToken.None);
                else if (team.OriginalRole is null)
                    await api.AddTeamMemberAsync(team.Id, new(summary.Id, Enum.Parse<TeamRole>(team.Selection)), CancellationToken.None);
                else
                    await api.UpdateTeamMemberAsync(team.Id, summary.Id, new(Enum.Parse<TeamRole>(team.Selection)), CancellationToken.None);
                team.AcceptChanges();
            }
            Message = "User and team access saved. Organisation role and activation changes take effect at the next token refresh or sign-in.";
        }
        catch (Exception ex)
        {
            Error = saved ? $"Account saved; some team changes remain unsaved. {ex.Message} Correct them and save again." : ex.Message;
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task ResetPasswordAsync()
    {
        if (IsLoading || _original is null) return;
        Error = Message = null;
        if (!ValidPassword()) return;
        IsLoading = true;
        try
        {
            await api.ResetUserPasswordAsync(_original.Id, Password, CancellationToken.None);
            Password = PasswordConfirmation = "";
            Message = "Password reset.";
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsLoading = false; }
    }
}

public sealed partial class UserDirectoryRow(UserSummary user) : ViewModelBase
{
    public Guid Id => user.Id;
    public string DisplayName => user.DisplayName;
    public string Email => user.Email;
    public bool IsActive => user.IsActive;
    [ObservableProperty] public partial bool IsSelected { get; set; }
}

public sealed partial class UserTeamRow(TeamDto team, TeamRole? role) : ViewModelBase
{
    public Guid Id => team.Id;
    public string Name => $"{team.Name} ({team.Key})" + (team.ArchivedAt is not null ? " · archived" : "");
    public TeamRole? OriginalRole { get; private set; } = role;
    public IReadOnlyList<string> Options { get; } = ["Not a member", "Viewer", "Member", "Lead"];
    [ObservableProperty] public partial string Selection { get; set; } = role?.ToString() ?? "Not a member";
    [ObservableProperty] public partial string OrgRole { get; set; } = "member";
    public bool IsDirty => Selection != (OriginalRole?.ToString() ?? "Not a member");
    public string EffectiveAccess => OrgRole is "owner" or "admin" ? "Administer" :
        Selection == "Not a member" ? "No access" : OrgRole == "guest" ? "Read & comment" :
        Selection switch { "Lead" => "Administer", "Member" => "Read, comment & edit", _ => "Read only" };
    partial void OnSelectionChanged(string value) => OnPropertyChanged(nameof(EffectiveAccess));
    partial void OnOrgRoleChanged(string value) => OnPropertyChanged(nameof(EffectiveAccess));
    public void AcceptChanges() => OriginalRole = Selection == "Not a member" ? null : Enum.Parse<TeamRole>(Selection);
}
