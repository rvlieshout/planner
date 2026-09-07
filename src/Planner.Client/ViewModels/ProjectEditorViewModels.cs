using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Planner.Client.Services;
using Planner.Contracts.Common;
using Planner.Contracts.Enums;
using Planner.Contracts.Projects;
using Planner.Contracts.Teams;

namespace Planner.Client.ViewModels;

/// <summary>The pieces the project page is assembled from: the three enums as things a combo box can
/// show, the colour swatches, the "no lead" case, and one editable milestone row.</summary>
public sealed record ProjectStatusOption(ProjectStatus Value, string Label)
{
    public static readonly IReadOnlyList<ProjectStatusOption> All =
    [
        new(ProjectStatus.Backlog, "Backlog"),
        new(ProjectStatus.Planned, "Planned"),
        new(ProjectStatus.InProgress, "In progress"),
        new(ProjectStatus.Paused, "Paused"),
        new(ProjectStatus.Completed, "Completed"),
        new(ProjectStatus.Canceled, "Canceled")
    ];

    public static ProjectStatusOption For(ProjectStatus status) => All.First(o => o.Value == status);
}

public sealed record ProjectHealthOption(ProjectHealth Value, string Label, string Color)
{
    public static readonly IReadOnlyList<ProjectHealthOption> All =
    [
        new(ProjectHealth.OnTrack, "On track", "#4CB782"),
        new(ProjectHealth.AtRisk, "At risk", "#F2C94C"),
        new(ProjectHealth.OffTrack, "Off track", "#EB5757")
    ];

    public static ProjectHealthOption For(ProjectHealth health) => All.First(o => o.Value == health);
}

public sealed record MilestoneStatusOption(MilestoneStatus Value, string Label)
{
    public static readonly IReadOnlyList<MilestoneStatusOption> All =
    [
        new(MilestoneStatus.Upcoming, "Upcoming"),
        new(MilestoneStatus.Active, "Active"),
        new(MilestoneStatus.Completed, "Completed")
    ];

    public static MilestoneStatusOption For(MilestoneStatus status) => All.First(o => o.Value == status);
}

/// <summary>A lead the combo box can show, including the absence of one. A plain nullable
/// <see cref="TeamMemberDto"/> can be displayed but not chosen: without a "No lead" row there is no way
/// back to unassigned once someone has been picked.</summary>
public sealed record LeadOption(TeamMemberDto? Member)
{
    public static readonly LeadOption None = new((TeamMemberDto?)null);

    public string Label => Member?.DisplayName ?? "No lead";
}

/// <summary>One colour in the project palette. The swatch knows whether it is the chosen one so the
/// tick can be bound rather than recomputed by a converter on every redraw.</summary>
public sealed partial class ColorSwatchViewModel(string value) : ViewModelBase
{
    public string Value { get; } = value;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>One milestone, editable in place.
///
/// Each row saves itself. A milestone is its own resource on the server — its own POST, PATCH and
/// DELETE — and rolling them into the project's Save button would mean one button standing for a batch
/// of independent requests, any of which can fail on its own. So the row carries its own busy state and
/// its own error, and the Save button lights up only once something in that row has actually changed.</summary>
public sealed partial class MilestoneRowViewModel : ViewModelBase
{
    private readonly PlannerApiClient _api;
    private readonly ILogger _logger;

    private MilestoneDto _milestone;

    private string _originalName;
    private DateOnly? _originalTargetDate;
    private MilestoneStatus _originalStatus;

    public MilestoneRowViewModel(PlannerApiClient api, ILogger logger, MilestoneDto milestone)
    {
        _api = api;
        _logger = logger;
        _milestone = milestone;

        _originalName = milestone.Name;
        _originalTargetDate = milestone.TargetDate;
        _originalStatus = milestone.Status;

        Name = milestone.Name;
        TargetDate = ToDateTime(milestone.TargetDate);
        SelectedStatus = MilestoneStatusOption.For(milestone.Status);
    }

    public Guid Id => _milestone.Id;

    public IReadOnlyList<MilestoneStatusOption> Statuses => MilestoneStatusOption.All;

    [ObservableProperty]
    public partial string? Name { get; set; }

    /// <summary>AtomUI's picker speaks <see cref="DateTime"/>; the contract speaks
    /// <see cref="DateOnly"/>. The conversion lives here rather than in a converter so the diff below
    /// compares the same type the server stores.</summary>
    [ObservableProperty]
    public partial DateTime? TargetDate { get; set; }

    [ObservableProperty]
    public partial MilestoneStatusOption? SelectedStatus { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    /// <summary>Read whenever the workspace is asked whether it may navigate away, so it has to hold
    /// for a row mid-edit: a cleared text box binds back as null, and a combo box with no selection
    /// means "unchanged" rather than an edit.</summary>
    public bool IsDirty =>
        NameValue != _originalName ||
        ToDateOnly(TargetDate) != _originalTargetDate ||
        StatusValue != _originalStatus;

    private string NameValue => Name?.Trim() ?? string.Empty;

    private MilestoneStatus StatusValue => SelectedStatus?.Value ?? _originalStatus;

    /// <summary>Issue rollup, computed by the server from the issues that point at this milestone.</summary>
    public string ProgressLabel =>
        _milestone.Progress.Total == 0
            ? "No issues"
            : $"{_milestone.Progress.Completed}/{_milestone.Progress.Total}";

    public double ProgressPercent => Math.Round(_milestone.Progress.Ratio * 100, 1);

    public event Action<MilestoneRowViewModel>? Deleted;

    partial void OnNameChanged(string? value) => NotifyDirty();

    partial void OnTargetDateChanged(DateTime? value) => NotifyDirty();

    partial void OnSelectedStatusChanged(MilestoneStatusOption? value) => NotifyDirty();

    private void NotifyDirty()
    {
        OnPropertyChanged(nameof(IsDirty));
        SaveCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync(CancellationToken ct)
    {
        var name = NameValue;

        if (string.IsNullOrEmpty(name))
        {
            Error = "Give the milestone a name.";
            return;
        }

        Error = null;
        IsBusy = true;

        try
        {
            var targetDate = ToDateOnly(TargetDate);

            var saved = await _api.UpdateMilestoneAsync(
                Id,
                new UpdateMilestoneRequest(
                    Name: When(name != _originalName, name),
                    Description: default,
                    TargetDate: When(targetDate != _originalTargetDate, targetDate),
                    Status: When(StatusValue != _originalStatus, StatusValue),
                    SortOrder: default),
                ct);

            Adopt(saved);
            _logger.LogInformation("Updated milestone {Name}", saved.Name);
        }
        catch (PlannerApiException ex)
        {
            Error = ex.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Could not save milestone {MilestoneId}", Id);
            Error = "Could not reach the server.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSave() => IsDirty;

    /// <summary>Deletes the milestone. Issues that pointed at it survive and fall back to the project,
    /// which is why this is offered at all where archiving a project is not.</summary>
    [RelayCommand]
    private async Task DeleteAsync(CancellationToken ct)
    {
        Error = null;
        IsBusy = true;

        try
        {
            await _api.DeleteMilestoneAsync(Id, ct);
            _logger.LogInformation("Deleted milestone {Name}", _originalName);
            Deleted?.Invoke(this);
        }
        catch (PlannerApiException ex)
        {
            Error = ex.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Could not delete milestone {MilestoneId}", Id);
            Error = "Could not reach the server.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Adopt(MilestoneDto saved)
    {
        _milestone = saved;
        _originalName = saved.Name;
        _originalTargetDate = saved.TargetDate;
        _originalStatus = saved.Status;

        Name = saved.Name;
        TargetDate = ToDateTime(saved.TargetDate);
        SelectedStatus = MilestoneStatusOption.For(saved.Status);

        OnPropertyChanged(nameof(ProgressLabel));
        OnPropertyChanged(nameof(ProgressPercent));
        NotifyDirty();
    }

    internal static DateTime? ToDateTime(DateOnly? date) => date?.ToDateTime(TimeOnly.MinValue);

    internal static DateOnly? ToDateOnly(DateTime? value) => value is { } v ? DateOnly.FromDateTime(v) : null;

    private static Optional<T> When<T>(bool changed, T value) => changed ? Optional<T>.From(value) : default;
}
