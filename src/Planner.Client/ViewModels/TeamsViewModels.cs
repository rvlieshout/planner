using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Planner.Client.Services;
using Planner.Contracts.Enums;
using Planner.Contracts.Teams;

namespace Planner.Client.ViewModels;

/// <summary>The pieces the teams page is assembled from: the team role as something a combo box can
/// show, one row in the team list, and one editable membership.</summary>
public sealed record TeamRoleOption(TeamRole Value, string Label, string Description)
{
    /// <summary>Ordered as a ladder, least authority first, because that is how someone reads a role
    /// they are about to grant. The descriptions are the API's permission matrix in a sentence each —
    /// see docs/roles-and-permissions.md.</summary>
    public static readonly IReadOnlyList<TeamRoleOption> All =
    [
        new(TeamRole.Viewer, "Viewer", "Reads the team's projects, issues and documents, and comments on them."),
        new(TeamRole.Member, "Member", "Reads and comments, and creates and edits projects, milestones, issues and documents."),
        new(TeamRole.Lead, "Lead", "Everything a member can do, plus the team's settings, membership, board columns and labels.")
    ];

    public static TeamRoleOption For(TeamRole role) => All.First(o => o.Value == role);
}

/// <summary>One team in the list on the left.
///
/// Holds the team it was built from so opening it costs no extra request, and takes on a saved one so
/// a rename, a recolour or an archive shows in the list without refetching it. The member count is
/// separate from that team: the server counted before this page added or removed anybody, and the page
/// knows better than the number it was handed.</summary>
public sealed partial class TeamListRow(TeamDto team) : ViewModelBase
{
    private TeamDto _team = team;

    public Guid Id => _team.Id;

    /// <summary>What the editor opens on. The list was fetched moments ago, and the PATCH it builds
    /// sends only what this user changed, so a field someone else has since edited is not overwritten
    /// by a stale copy of it.</summary>
    internal TeamDto Team => _team;

    public string Name => _team.Name;

    public string Key => _team.Key;

    public string Color => _team.Color;

    public bool IsArchived => _team.ArchivedAt is not null;

    public string Detail
    {
        get
        {
            var parts = new List<string> { MemberCount == 1 ? "1 member" : $"{MemberCount} members" };

            if (_team.IsPrivate)
            {
                parts.Add("Private");
            }

            if (IsArchived)
            {
                parts.Add("Archived");
            }

            return string.Join(" · ", parts);
        }
    }

    [ObservableProperty]
    public partial int MemberCount { get; set; } = team.MemberCount;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public void Adopt(TeamDto saved)
    {
        _team = saved;
        MemberCount = saved.MemberCount;

        foreach (var name in new[] { nameof(Name), nameof(Key), nameof(Color), nameof(IsArchived), nameof(Detail) })
        {
            OnPropertyChanged(name);
        }
    }

    partial void OnMemberCountChanged(int value) => OnPropertyChanged(nameof(Detail));
}

/// <summary>One membership, editable in place.
///
/// A membership is its own resource on the server — its own POST, PATCH and DELETE — so the row owns
/// its request, its busy state and its error, and its tick appears only once the role in it has
/// actually changed. What the row does not own is *when* it is saved: the page's Save button drives
/// every dirty row, because a button called "Save changes" on a page showing an unsaved mark has to be
/// able to clear it. Removal is the row's own, and immediate — like deleting a milestone, it is not a
/// field to be edited and then saved.
///
/// Two refusals arrive from the server rather than being predicted here, and both are shown as it
/// wrote them: demoting or removing a team's only lead.</summary>
public sealed partial class TeamMemberRowViewModel : ViewModelBase
{
    private readonly PlannerApiClient _api;
    private readonly ILogger _logger;

    private TeamRole _originalRole;

    public TeamMemberRowViewModel(PlannerApiClient api, ILogger logger, TeamMemberDto member)
    {
        _api = api;
        _logger = logger;

        TeamId = member.TeamId;
        UserId = member.UserId;
        DisplayName = member.DisplayName;
        Email = member.Email;
        Initials = ViewModels.Initials.Of(member.DisplayName);

        _originalRole = member.Role;
        SelectedRole = TeamRoleOption.For(member.Role);
    }

    public Guid TeamId { get; }

    public Guid UserId { get; }

    public string DisplayName { get; }

    public string Email { get; }

    public string Initials { get; }

    /// <summary>The role the server holds, as opposed to the one the combo box is showing.</summary>
    public TeamRole Role => _originalRole;

    public IReadOnlyList<TeamRoleOption> Roles => TeamRoleOption.All;

    [ObservableProperty]
    public partial TeamRoleOption? SelectedRole { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    /// <summary>Read whenever the workspace is asked whether it may navigate away, so it has to hold
    /// for a row mid-edit: a combo box with no selection means "unchanged" rather than an edit.</summary>
    public bool IsDirty => RoleValue != _originalRole;

    private TeamRole RoleValue => SelectedRole?.Value ?? _originalRole;

    public event Action<TeamMemberRowViewModel>? Removed;

    partial void OnSelectedRoleChanged(TeamRoleOption? value)
    {
        OnPropertyChanged(nameof(IsDirty));
        SaveCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Saves this row. Internal because the page's Save button drives it directly rather than
    /// through the command — it needs to await each row in turn and see which ones stayed dirty.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    internal async Task SaveAsync(CancellationToken ct)
    {
        Error = null;
        IsBusy = true;

        try
        {
            var saved = await _api.UpdateTeamMemberAsync(
                TeamId, UserId, new UpdateTeamMemberRequest(RoleValue), ct);

            _originalRole = saved.Role;
            SelectedRole = TeamRoleOption.For(saved.Role);

            OnPropertyChanged(nameof(Role));
            _logger.LogInformation("{Member} is now a {Role}", DisplayName, saved.Role);
        }
        catch (PlannerApiException ex)
        {
            Error = ex.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Could not save the membership of {UserId}", UserId);
            Error = "Could not reach the server.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSave() => IsDirty;

    /// <summary>Removes the membership. The account itself is untouched, and so is everything the
    /// person has written in the team — that is the difference between this and deactivating them,
    /// which is the users page's business.</summary>
    [RelayCommand]
    private async Task RemoveAsync(CancellationToken ct)
    {
        Error = null;
        IsBusy = true;

        try
        {
            await _api.RemoveTeamMemberAsync(TeamId, UserId, ct);
            _logger.LogInformation("Removed {Member} from the team", DisplayName);
            Removed?.Invoke(this);
        }
        catch (PlannerApiException ex)
        {
            Error = ex.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Could not remove the membership of {UserId}", UserId);
            Error = "Could not reach the server.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
