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

public sealed partial class LabelChipViewModel(LabelDto label) : ViewModelBase
{
    public LabelDto Label { get; } = label;

    public Guid Id => Label.Id;

    public string Name => Label.Name;

    public string Color => Label.Color;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Filled with the label's own colour when picked. Left to the theme, a checked tag takes
    /// the primary colour, so every selected label would look identical.</summary>
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
        SelectedAssignee = AssigneeOption.Unassigned;
        SelectedProject = ProjectOption.None;
        SelectedMilestone = MilestoneOption.None;
        SelectedEstimate = EstimateOption.None;
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

    public Guid? DefaultParentId { get; set; }

    public bool IsReady { get; private set; }

    public bool HasUnsavedChanges => IsReady && (TitleValue != _originalTitle ||
        DescriptionValue != _originalDescription || StateId != _originalStateId ||
        PriorityValue != _originalPriority || AssigneeId != _originalAssigneeId ||
        ProjectId != _originalProjectId || MilestoneId != _originalMilestoneId ||
        EstimateValue != _originalEstimate ||
        !SelectedLabelIds().OrderBy(x => x).SequenceEqual(_originalLabelIds.OrderBy(x => x)));

    [ObservableProperty]
    public partial string IssueKey { get; set; } = string.Empty;

    public IReadOnlyList<PriorityOption> Priorities => PriorityOption.All;

    public ObservableCollection<WorkflowStateDto> States { get; } = [];

    public ObservableCollection<AssigneeOption> Assignees { get; } = [AssigneeOption.Unassigned];

    public ObservableCollection<ProjectOption> Projects { get; } = [ProjectOption.None];

    public ObservableCollection<MilestoneOption> Milestones { get; } = [MilestoneOption.None];

    public ObservableCollection<EstimateOption> Estimates { get; } = [];

    public ObservableCollection<LabelChipViewModel> Labels { get; } = [];

    [ObservableProperty]
    public partial string? IssueTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Description { get; set; } = string.Empty;

    [ObservableProperty]
    public partial WorkflowStateDto? SelectedState { get; set; }

    // Every one of these is nullable because a combo box whose ItemsSource is refilled writes a null
    // selection back down the binding; the accessors below read "nothing selected" as "unchanged".
    [ObservableProperty]
    public partial PriorityOption? SelectedPriority { get; set; }

    [ObservableProperty]
    public partial AssigneeOption? SelectedAssignee { get; set; }

    [ObservableProperty]
    public partial ProjectOption? SelectedProject { get; set; }

    [ObservableProperty]
    public partial MilestoneOption? SelectedMilestone { get; set; }

    [ObservableProperty]
    public partial EstimateOption? SelectedEstimate { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingOptions { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    public bool HasLabels => Labels.Count > 0;

    public Avalonia.Media.Geometry? LabelIcon => Controls.AppIcons.Label;

    /// <summary>A milestone belongs to a project, so the pill only appears once there is one.</summary>
    public bool HasProject => SelectedProject?.Project is not null;

    private string TitleValue => IssueTitle?.Trim() ?? string.Empty;

    private string? DescriptionValue =>
        string.IsNullOrWhiteSpace(Description) ? null : Description;

    private Guid StateId => SelectedState?.Id ?? _originalStateId;

    private IssuePriority PriorityValue => SelectedPriority?.Value ?? _originalPriority;

    /// <summary>Nothing selected means unchanged; the explicit empty option — "Unassigned", "No
    /// project" — means clear it. Collapsing those two would make the field unclearable.</summary>
    private Guid? AssigneeId => SelectedAssignee is { } option ? option.Member?.UserId : _originalAssigneeId;

    private Guid? ProjectId => SelectedProject is { } option ? option.Project?.Id : _originalProjectId;

    private Guid? MilestoneId => SelectedMilestone is { } option ? option.Milestone?.Id : _originalMilestoneId;

    private int? EstimateValue => SelectedEstimate is { } option ? option.Value : _originalEstimate;

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
                    SelectedProject =
                        Projects.FirstOrDefault(p => p.Project?.Id == preselect) ?? ProjectOption.None;
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
            Assignees.Add(new AssigneeOption(member));
        }

        foreach (var project in (await _api.GetProjectsAsync(_teamId, ct)).Items)
        {
            Projects.Add(new ProjectOption(project));
        }

        foreach (var label in await _api.GetTeamLabelsAsync(_teamId, ct))
        {
            Labels.Add(new LabelChipViewModel(label));
        }

        Estimates.Add(EstimateOption.None);

        foreach (var points in EstimateOption.Scale)
        {
            Estimates.Add(new EstimateOption(points));
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
        SelectedAssignee = Assignees.FirstOrDefault(a => a.Member?.UserId == issue.Assignee?.Id)
                           ?? AssigneeOption.Unassigned;
        SelectedProject = Projects.FirstOrDefault(p => p.Project?.Id == issue.ProjectId)
                          ?? ProjectOption.None;
        SelectedEstimate = EstimateFor(issue.Estimate);

        foreach (var chip in Labels)
        {
            chip.IsSelected = issue.Labels.Any(l => l.Id == chip.Id);
        }

        if (SelectedProject.Project is { } project)
        {
            await LoadMilestonesAsync(project.Id, ct);
            SelectedMilestone = Milestones.FirstOrDefault(m => m.Milestone?.Id == issue.MilestoneId)
                                ?? MilestoneOption.None;
        }

        _populating = false;

        _originalTitle = TitleValue;
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
        OnPropertyChanged(nameof(HasProject));
        IsReady = true;
    }

    /// <summary>The estimate the issue actually carries, adding it to the scale when it is not on it —
    /// so an issue pointed by some other means keeps its number instead of being rounded away by the
    /// act of opening the form.</summary>
    private EstimateOption EstimateFor(int? estimate)
    {
        if (estimate is not { } points)
        {
            return EstimateOption.None;
        }

        if (Estimates.FirstOrDefault(e => e.Value == points) is { } known)
        {
            return known;
        }

        var option = new EstimateOption(points);
        var at = Estimates.TakeWhile(e => e.Value is { } v && v < points).Count();
        Estimates.Insert(Math.Max(at, 1), option);

        return option;
    }

    partial void OnSelectedProjectChanged(ProjectOption? value)
    {
        OnPropertyChanged(nameof(HasProject));

        if (_populating)
        {
            return;
        }

        // Explicitly "no milestone" rather than no selection: the milestone the form was holding
        // belonged to the project that was just swapped out, and must not survive it.
        Milestones.Clear();
        Milestones.Add(MilestoneOption.None);
        SelectedMilestone = MilestoneOption.None;

        if (value?.Project is { } project)
        {
            _ = LoadMilestonesAsync(project.Id, CancellationToken.None);
        }
    }

    private async Task LoadMilestonesAsync(Guid projectId, CancellationToken ct)
    {
        try
        {
            var milestones = await _api.GetMilestonesAsync(projectId, ct);

            Milestones.Clear();
            Milestones.Add(MilestoneOption.None);

            foreach (var milestone in milestones)
            {
                Milestones.Add(new MilestoneOption(milestone));
            }

            SelectedMilestone ??= MilestoneOption.None;
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

        if (IsEditing && !IsReady) return;

        if (string.IsNullOrEmpty(TitleValue))
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
            _originalTitle = TitleValue;
            _originalDescription = DescriptionValue;
            _originalStateId = StateId;
            _originalPriority = PriorityValue;
            _originalAssigneeId = AssigneeId;
            _originalProjectId = ProjectId;
            _originalMilestoneId = MilestoneId;
            _originalEstimate = EstimateValue;
            _originalLabelIds = SelectedLabelIds();
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
        if (_issueId is not { } issueId || !IsReady)
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
            TitleValue,
            DescriptionValue,
            SelectedState?.Id,
            PriorityValue,
            AssigneeId,
            ProjectId,
            MilestoneId,
            DefaultParentId,
            EstimateValue,
            null,
            labelIds.Count == 0 ? null : labelIds);
    }

    /// <summary>Builds the smallest PATCH that expresses what the user changed. Anything untouched is
    /// left unset, which the serializer omits entirely — so the server never sees it and never
    /// overwrites it with a value this form happened to be holding.</summary>
    private UpdateIssueRequest BuildPatch()
    {
        var title = TitleValue;
        var description = DescriptionValue;
        var labelIds = SelectedLabelIds();

        return new UpdateIssueRequest(
            Title: When(title != _originalTitle, title),
            Description: When(description != _originalDescription, description),
            StateId: When(StateId != _originalStateId, StateId),
            Priority: When(PriorityValue != _originalPriority, PriorityValue),
            AssigneeId: When(AssigneeId != _originalAssigneeId, AssigneeId),
            ProjectId: When(ProjectId != _originalProjectId, ProjectId),
            MilestoneId: When(MilestoneId != _originalMilestoneId, MilestoneId),
            ParentId: default,
            Estimate: When(EstimateValue != _originalEstimate, EstimateValue),
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
