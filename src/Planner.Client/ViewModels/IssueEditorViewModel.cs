using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Planner.Client.Services;
using Planner.Contracts.Common;
using Planner.Contracts.Enums;
using Planner.Contracts.Issues;
using Planner.Contracts.Projects;
using Planner.Contracts.Teams;

namespace Planner.Client.ViewModels;

/// <summary>A priority the combo box can show, since the enum member alone reads badly in a UI.</summary>
public sealed record PriorityOption(IssuePriority Value, string Label)
{
    public static readonly IReadOnlyList<PriorityOption> All =
    [
        new(IssuePriority.None, "No priority"),
        new(IssuePriority.Urgent, "Urgent"),
        new(IssuePriority.High, "High"),
        new(IssuePriority.Medium, "Medium"),
        new(IssuePriority.Low, "Low")
    ];

    public static PriorityOption For(IssuePriority priority) =>
        All.First(p => p.Value == priority);
}

public sealed partial class LabelChipViewModel(LabelDto label) : ViewModelBase
{
    public LabelDto Label { get; } = label;

    public Guid Id => Label.Id;

    public string Name => Label.Name;

    public string Color => Label.Color;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Filled with the label's own colour when picked. Left to the theme, a checked
    /// ToggleButton takes the system accent, so every selected label would look identical.</summary>
    public string ChipBackground => IsSelected ? Color : "#00FFFFFF";

    public string ChipForeground => IsSelected ? "#FFFFFF" : Color;

    partial void OnIsSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(ChipBackground));
        OnPropertyChanged(nameof(ChipForeground));
    }

    [RelayCommand]
    private void Toggle() => IsSelected = !IsSelected;
}

/// <summary>The issue form, in both directions.
///
/// Creating: only the title is required. Everything else has a sensible default on the server — the
/// team's default workflow state, no priority, unassigned — so an issue can be captured in two
/// keystrokes and filled in later, which is the difference between a tracker people use and one they
/// route around.
///
/// Editing: the same form, populated from the issue, saving a diff. Only fields the user actually
/// changed are sent, so two people editing different fields of the same issue do not overwrite each
/// other, and the server's own concurrency check has something honest to compare.</summary>
public sealed partial class IssueEditorViewModel : ViewModelBase
{
    private readonly PlannerApiClient _api;
    private readonly ILogger _logger;
    private readonly Guid _teamId;
    private readonly Guid? _issueId;

    private bool _populating;

    // What the issue looked like when the form opened, for the diff on save.
    private string _originalTitle = string.Empty;
    private string? _originalDescription;
    private Guid _originalStateId;
    private IssuePriority _originalPriority;
    private Guid? _originalAssigneeId;
    private Guid? _originalProjectId;
    private Guid? _originalMilestoneId;
    private int? _originalEstimate;
    private IReadOnlyList<Guid> _originalLabelIds = [];

    private IssueEditorViewModel(
        PlannerApiClient api,
        ILogger logger,
        Guid teamId,
        string teamName,
        Guid? issueId)
    {
        _api = api;
        _logger = logger;
        _teamId = teamId;
        _issueId = issueId;

        TeamName = teamName;
        SelectedPriority = PriorityOption.All[0];
    }

    public static IssueEditorViewModel ForCreate(
        PlannerApiClient api,
        ILogger logger,
        Guid teamId,
        string teamName,
        Guid? defaultProjectId = null) =>
        new(api, logger, teamId, teamName, null) { DefaultProjectId = defaultProjectId };

    public static IssueEditorViewModel ForEdit(
        PlannerApiClient api,
        ILogger logger,
        Guid teamId,
        string teamName,
        Guid issueId) =>
        new(api, logger, teamId, teamName, issueId);

    public bool IsEditing => _issueId is not null;

    public string TeamName { get; }

    public string Heading => IsEditing ? IssueKey : "New issue";

    /// <summary>The dialog's own title bar and taskbar entry.</summary>
    public string WindowTitle => IsEditing
        ? $"{IssueKey} — Planner"
        : "New Issue — Planner";

    public string SubmitLabel => IsEditing ? "Save changes" : "Create issue";

    private Guid? DefaultProjectId { get; init; }

    [ObservableProperty]
    public partial string IssueKey { get; set; } = string.Empty;

    public IReadOnlyList<PriorityOption> Priorities => PriorityOption.All;

    public ObservableCollection<WorkflowStateDto> States { get; } = [];

    public ObservableCollection<TeamMemberDto> Assignees { get; } = [];

    public ObservableCollection<ProjectDto> Projects { get; } = [];

    public ObservableCollection<MilestoneDto> Milestones { get; } = [];

    public ObservableCollection<LabelChipViewModel> Labels { get; } = [];

    [ObservableProperty]
    public partial string IssueTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;

    [ObservableProperty]
    public partial WorkflowStateDto? SelectedState { get; set; }

    [ObservableProperty]
    public partial PriorityOption SelectedPriority { get; set; }

    [ObservableProperty]
    public partial TeamMemberDto? SelectedAssignee { get; set; }

    [ObservableProperty]
    public partial ProjectDto? SelectedProject { get; set; }

    [ObservableProperty]
    public partial MilestoneDto? SelectedMilestone { get; set; }

    [ObservableProperty]
    public partial decimal? Estimate { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingOptions { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    public bool HasLabels => Labels.Count > 0;

    /// <summary>Raised with the saved issue so the current view can show it without waiting for the
    /// socket to echo it back.</summary>
    public event Action<IssueSummary>? Saved;

    public event Action? Cancelled;

    public async Task LoadAsync(CancellationToken ct)
    {
        IsLoadingOptions = true;

        try
        {
            await LoadOptionsAsync(ct);

            if (_issueId is { } issueId)
            {
                await PopulateFromIssueAsync(issueId, ct);
            }
            else
            {
                SelectedState = States.FirstOrDefault(s => s.IsDefault) ?? States.FirstOrDefault();

                if (DefaultProjectId is { } preselect)
                {
                    SelectedProject = Projects.FirstOrDefault(p => p.Id == preselect);
                }
            }
        }
        catch (Exception ex) when (ex is PlannerApiException or HttpRequestException)
        {
            _logger.LogWarning(ex, "Could not load the issue form");
            Error = IsEditing
                ? "Could not load this issue."
                : "Some options could not be loaded. You can still create the issue.";
        }
        finally
        {
            IsLoadingOptions = false;
        }
    }

    private async Task LoadOptionsAsync(CancellationToken ct)
    {
        foreach (var state in (await _api.GetWorkflowStatesAsync(_teamId, ct)).OrderBy(s => s.Position))
        {
            States.Add(state);
        }

        foreach (var member in await _api.GetTeamMembersAsync(_teamId, ct))
        {
            Assignees.Add(member);
        }

        foreach (var project in (await _api.GetProjectsAsync(_teamId, ct)).Items)
        {
            Projects.Add(project);
        }

        foreach (var label in await _api.GetTeamLabelsAsync(_teamId, ct))
        {
            Labels.Add(new LabelChipViewModel(label));
        }

        OnPropertyChanged(nameof(HasLabels));
    }

    private async Task PopulateFromIssueAsync(Guid issueId, CancellationToken ct)
    {
        var issue = await _api.GetIssueAsync(issueId, ct);

        // Suppress the reactive milestone reload while filling the form in, otherwise setting the
        // project would immediately clear the milestone we are about to restore.
        _populating = true;

        IssueKey = issue.Key;
        IssueTitle = issue.Title;
        Description = issue.Description ?? string.Empty;
        SelectedState = States.FirstOrDefault(s => s.Id == issue.StateId);
        SelectedPriority = PriorityOption.For(issue.Priority);
        SelectedAssignee = Assignees.FirstOrDefault(a => a.UserId == issue.Assignee?.Id);
        SelectedProject = Projects.FirstOrDefault(p => p.Id == issue.ProjectId);
        Estimate = issue.Estimate;

        foreach (var chip in Labels)
        {
            chip.IsSelected = issue.Labels.Any(l => l.Id == chip.Id);
        }

        if (SelectedProject is { } project)
        {
            await LoadMilestonesAsync(project.Id, ct);
            SelectedMilestone = Milestones.FirstOrDefault(m => m.Id == issue.MilestoneId);
        }

        _populating = false;

        _originalTitle = IssueTitle;
        _originalDescription = issue.Description;
        _originalStateId = issue.StateId;
        _originalPriority = issue.Priority;
        _originalAssigneeId = issue.Assignee?.Id;
        _originalProjectId = issue.ProjectId;
        _originalMilestoneId = issue.MilestoneId;
        _originalEstimate = issue.Estimate;
        _originalLabelIds = issue.Labels.Select(l => l.Id).ToList();

        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(WindowTitle));
    }

    partial void OnSelectedProjectChanged(ProjectDto? value)
    {
        if (_populating)
        {
            return;
        }

        Milestones.Clear();
        SelectedMilestone = null;

        if (value is not null)
        {
            _ = LoadMilestonesAsync(value.Id, CancellationToken.None);
        }
    }

    private async Task LoadMilestonesAsync(Guid projectId, CancellationToken ct)
    {
        try
        {
            Milestones.Clear();

            foreach (var milestone in await _api.GetMilestonesAsync(projectId, ct))
            {
                Milestones.Add(milestone);
            }
        }
        catch (Exception ex) when (ex is PlannerApiException or HttpRequestException)
        {
            _logger.LogWarning(ex, "Could not load milestones for project {ProjectId}", projectId);
        }
    }

    [RelayCommand]
    private async Task SubmitAsync(CancellationToken ct)
    {
        Error = null;

        if (string.IsNullOrWhiteSpace(IssueTitle))
        {
            Error = "Give the issue a title.";
            return;
        }

        IsBusy = true;

        try
        {
            var saved = _issueId is { } issueId
                ? await _api.UpdateIssueAsync(issueId, BuildPatch(), ct)
                : await _api.CreateIssueAsync(BuildCreate(), ct);

            _logger.LogInformation("{Action} {Key}", IsEditing ? "Updated" : "Created", saved.Key);
            Saved?.Invoke(saved);
        }
        catch (PlannerApiException ex)
        {
            // The API's problem detail is written for a person — "You can only assign issues to members
            // of the issue's team" beats anything this form could invent.
            Error = ex.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Could not save the issue");
            Error = "Could not reach the server.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Archives rather than deletes: the everyday "this is done with" action, and the one a
    /// team member is allowed to take. Deleting outright needs team-lead authority and is not offered
    /// here.</summary>
    [RelayCommand]
    private async Task ArchiveAsync(CancellationToken ct)
    {
        if (_issueId is not { } issueId)
        {
            return;
        }

        Error = null;
        IsBusy = true;

        try
        {
            var archived = await _api.ArchiveIssueAsync(issueId, ct);
            _logger.LogInformation("Archived {Key}", archived.Key);
            Saved?.Invoke(archived);
        }
        catch (PlannerApiException ex)
        {
            Error = ex.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Could not archive the issue");
            Error = "Could not reach the server.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private CreateIssueRequest BuildCreate()
    {
        var labelIds = SelectedLabelIds();

        return new CreateIssueRequest(
            _teamId,
            IssueTitle.Trim(),
            string.IsNullOrWhiteSpace(Description) ? null : Description,
            SelectedState?.Id,
            SelectedPriority.Value,
            SelectedAssignee?.UserId,
            SelectedProject?.Id,
            SelectedMilestone?.Id,
            null,
            Estimate is null ? null : (int)Estimate,
            null,
            labelIds.Count == 0 ? null : labelIds);
    }

    /// <summary>Builds the smallest PATCH that expresses what the user changed. Anything untouched is
    /// left unset, which the serializer omits entirely — so the server never sees it and never
    /// overwrites it with a value this form happened to be holding.</summary>
    private UpdateIssueRequest BuildPatch()
    {
        var title = IssueTitle.Trim();
        var description = string.IsNullOrWhiteSpace(Description) ? null : Description;
        var stateId = SelectedState?.Id ?? _originalStateId;
        var assigneeId = SelectedAssignee?.UserId;
        var projectId = SelectedProject?.Id;
        var milestoneId = SelectedMilestone?.Id;
        var estimate = Estimate is null ? (int?)null : (int)Estimate;
        var labelIds = SelectedLabelIds();

        return new UpdateIssueRequest(
            Title: When(title != _originalTitle, title),
            Description: When(description != _originalDescription, description),
            StateId: When(stateId != _originalStateId, stateId),
            Priority: When(SelectedPriority.Value != _originalPriority, SelectedPriority.Value),
            AssigneeId: When(assigneeId != _originalAssigneeId, assigneeId),
            ProjectId: When(projectId != _originalProjectId, projectId),
            MilestoneId: When(milestoneId != _originalMilestoneId, milestoneId),
            ParentId: default,
            Estimate: When(estimate != _originalEstimate, estimate),
            DueDate: default,
            SortOrder: default,
            LabelIds: When(!labelIds.OrderBy(id => id).SequenceEqual(_originalLabelIds.OrderBy(id => id)),
                (IReadOnlyList<Guid>)labelIds));
    }

    private static Optional<T> When<T>(bool changed, T value) => changed ? Optional<T>.From(value) : default;

    private List<Guid> SelectedLabelIds() => Labels.Where(l => l.IsSelected).Select(l => l.Id).ToList();

    [RelayCommand]
    private void Cancel() => Cancelled?.Invoke();
}
