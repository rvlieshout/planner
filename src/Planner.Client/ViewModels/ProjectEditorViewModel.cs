using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Planner.Client.Services;
using Planner.Contracts.Common;
using Planner.Contracts.Enums;
using Planner.Contracts.Projects;
using Planner.Contracts.Teams;

namespace Planner.Client.ViewModels;

/// <summary>The project form, in both directions, as a page rather than a dialog.
///
/// A project is not a thing you fill in and dismiss. It has milestones underneath it, each of its own
/// resource on the server, and maintaining those is a session rather than a single answer — so the form
/// takes the content pane, sits under the same toolbar and status bar as every other view, and stays
/// open while the work is done. That is also what lets creating flow straight into filling in: a
/// successful create turns the page into the edit page for the project it just made, and the milestone
/// section — which needs an id to POST to — comes to life underneath.
///
/// Editing saves a diff, for the same reason the issue form does: only fields the user actually changed
/// are sent, so two people editing different fields of the same project do not overwrite each other.</summary>
public sealed partial class ProjectEditorViewModel : ViewModelBase, IWorkspaceContent, IUnsavedWork
{
    /// <summary>The palette a project can be labelled with. Ten is enough to tell a sidebar full of
    /// projects apart and few enough that the choice is a glance rather than a colour wheel; anything
    /// else can still be typed in as hex.</summary>
    private static readonly string[] Palette =
    [
        "#5E6AD2", "#26B5CE", "#4CB782", "#0F7B6C", "#F2C94C",
        "#F2994A", "#EB5757", "#BB87FC", "#D4A27F", "#95A2B3"
    ];

    private readonly PlannerApiClient _api;
    private readonly ILogger _logger;
    private readonly Guid _teamId;
    private readonly string _teamName;

    private bool _populating;

    // What the project looked like when the page opened, for the diff on save.
    private string _originalName = string.Empty;
    private string? _originalSummary;
    private string? _originalDescription;
    private ProjectStatus _originalStatus;
    private ProjectHealth _originalHealth;
    private string _originalColor = Palette[0];
    private Guid? _originalLeadId;
    private DateOnly? _originalStartDate;
    private DateOnly? _originalTargetDate;

    private ProjectEditorViewModel(
        PlannerApiClient api,
        ILogger logger,
        Guid teamId,
        string teamName,
        Guid? projectId)
    {
        _api = api;
        _logger = logger;
        _teamId = teamId;
        _teamName = teamName;

        ProjectId = projectId;

        SelectedStatus = ProjectStatusOption.All[0];
        SelectedHealth = ProjectHealthOption.All[0];
        SelectedLead = LeadOption.None;
        NewMilestoneStatus = MilestoneStatusOption.All[0];

        foreach (var color in Palette)
        {
            Swatches.Add(new ColorSwatchViewModel(color));
        }

        ProjectColor = Palette[0];

        // Rows are watched by virtue of being in the collection, not by having been added through the
        // right helper: one path that appends a row directly would otherwise leave it unwatched, and
        // its unsaved edits invisible to the navigator's guard.
        Milestones.CollectionChanged += OnMilestonesCollectionChanged;
    }

    public static ProjectEditorViewModel ForCreate(
        PlannerApiClient api, ILogger logger, Guid teamId, string teamName) =>
        new(api, logger, teamId, teamName, null);

    public static ProjectEditorViewModel ForEdit(
        PlannerApiClient api, ILogger logger, Guid teamId, string teamName, Guid projectId) =>
        new(api, logger, teamId, teamName, projectId);

    /// <summary>Null until the project exists. Set by a successful create, which is the moment the page
    /// stops being a new-project form and becomes that project's settings.</summary>
    public Guid? ProjectId { get; private set; }

    public bool IsEditing => ProjectId is not null;

    public string Title => IsEditing ? _originalName : "New project";

    public string? Subtitle => IsEditing ? "Project settings" : _teamName;

    public string StatusSummary => ProjectId is null
        ? "Not saved yet"
        : Milestones.Count switch
        {
            0 => "No milestones",
            1 => "1 milestone",
            var n => $"{n} milestones"
        };

    public string SaveLabel => IsEditing ? "Save changes" : "Create project";

    public IReadOnlyList<ProjectStatusOption> Statuses => ProjectStatusOption.All;

    public IReadOnlyList<ProjectHealthOption> Healths => ProjectHealthOption.All;

    public IReadOnlyList<MilestoneStatusOption> MilestoneStatuses => MilestoneStatusOption.All;

    public ObservableCollection<LeadOption> Leads { get; } = [LeadOption.None];

    public ObservableCollection<ColorSwatchViewModel> Swatches { get; } = [];

    public ObservableCollection<MilestoneRowViewModel> Milestones { get; } = [];

    [ObservableProperty]
    public partial string ProjectName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ProjectStatusOption? SelectedStatus { get; set; }

    [ObservableProperty]
    public partial ProjectHealthOption? SelectedHealth { get; set; }

    [ObservableProperty]
    public partial LeadOption? SelectedLead { get; set; }

    [ObservableProperty]
    public partial string? ProjectColor { get; set; }

    [ObservableProperty]
    public partial DateTime? StartDate { get; set; }

    [ObservableProperty]
    public partial DateTime? TargetDate { get; set; }

    [ObservableProperty]
    public partial string NewMilestoneName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial DateTime? NewMilestoneTargetDate { get; set; }

    [ObservableProperty]
    public partial MilestoneStatusOption? NewMilestoneStatus { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    /// <summary>Cleared by the next edit, so it says "this is saved", not "this was saved at some
    /// point".</summary>
    [ObservableProperty]
    public partial string? Notice { get; set; }

    public bool HasMilestones => Milestones.Count > 0;

    /// <summary>Everything on this page the server has not been told about: the form itself, any
    /// milestone row edited but not saved, and a milestone typed into the add row but never added.
    /// Computed rather than tracked, so it cannot fall out of step with the fields it describes.</summary>
    public bool HasUnsavedChanges =>
        IsFormDirty ||
        Milestones.Any(m => m.IsDirty) ||
        !string.IsNullOrWhiteSpace(NewMilestoneName);

    public string UnsavedSummary => IsEditing
        ? $"“{_originalName}” has changes that have not been saved."
        : "This project has not been created yet.";

    /// <summary>The same comparison the PATCH is built from, asked as a yes or no. For a project that
    /// does not exist yet the originals are the empty form, so an untouched new-project page is clean
    /// and one with a name typed into it is not.</summary>
    private bool IsFormDirty =>
        NameValue != _originalName ||
        Blank(Summary) != _originalSummary ||
        Blank(Description) != _originalDescription ||
        StatusValue != _originalStatus ||
        HealthValue != _originalHealth ||
        ColorValue != _originalColor ||
        LeadId != _originalLeadId ||
        MilestoneRowViewModel.ToDateOnly(StartDate) != _originalStartDate ||
        MilestoneRowViewModel.ToDateOnly(TargetDate) != _originalTargetDate;

    /// <summary>What the form currently says, read once and used by both the dirty check and the
    /// request it builds, so the two can never disagree.
    ///
    /// A control holding nothing reads as "unchanged" rather than as an edit. That is not
    /// hypothetical: refilling the collection a combo box draws from empties it on the way through,
    /// and an empty combo box writes a null selection back down the binding — on whose notification
    /// the dirty check then runs.</summary>
    private string NameValue => Trimmed(ProjectName);

    private ProjectStatus StatusValue => SelectedStatus?.Value ?? _originalStatus;

    private ProjectHealth HealthValue => SelectedHealth?.Value ?? _originalHealth;

    /// <summary>A blank colour box means "leave it alone", not "no colour": the swatches are how a
    /// project's colour is changed, and the server has no use for an empty string.</summary>
    private string ColorValue => Blank(ProjectColor) ?? _originalColor;

    /// <summary>No selection at all means unchanged; the "No lead" option, which is a selection with
    /// no member behind it, means clear the lead. Collapsing those two would make a lead
    /// unclearable.</summary>
    private Guid? LeadId => SelectedLead is { } lead ? lead.Member?.UserId : _originalLeadId;

    public Avalonia.Media.Geometry? PlusIcon => Controls.AppIcons.Plus;

    public Avalonia.Media.Geometry? CheckIcon => Controls.AppIcons.Check;

    public Avalonia.Media.Geometry? TrashIcon => Controls.AppIcons.Trash;

    /// <summary>Raised after every successful save so the sidebar can pick up a new project, or a
    /// renamed or recoloured one, without refetching the whole navigation.</summary>
    public event Action<ProjectDto>? Saved;

    /// <summary>The page's way of saying it is finished; the workspace decides where to go next.</summary>
    public event Action? Closed;

    public async Task LoadAsync(CancellationToken ct)
    {
        IsLoading = true;
        Error = null;

        try
        {
            await LoadLeadsAsync(ct);

            if (ProjectId is { } projectId)
            {
                Apply(await _api.GetProjectAsync(projectId, ct));
                await LoadMilestonesAsync(projectId, ct);
            }
        }
        catch (Exception ex) when (ex is PlannerApiException or HttpRequestException)
        {
            _logger.LogWarning(ex, "Could not load the project page");
            Error = IsEditing
                ? "Could not load this project."
                : "Some options could not be loaded. You can still create the project.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadLeadsAsync(CancellationToken ct)
    {
        var members = await _api.GetTeamMembersAsync(_teamId, ct);

        // Remember the selection across the refill: emptying the collection empties the combo box,
        // which writes its now-invalid selection back as null.
        var selected = SelectedLead?.Member?.UserId;

        Leads.Clear();
        Leads.Add(LeadOption.None);

        foreach (var member in members)
        {
            Leads.Add(new LeadOption(member));
        }

        SelectedLead = Leads.FirstOrDefault(l => l.Member?.UserId == selected) ?? LeadOption.None;
    }

    private async Task LoadMilestonesAsync(Guid projectId, CancellationToken ct)
    {
        // Clear() raises a Reset that carries no OldItems, so the rows are let go by hand first.
        foreach (var row in Milestones)
        {
            Detach(row);
        }

        Milestones.Clear();

        foreach (var milestone in await _api.GetMilestonesAsync(projectId, ct))
        {
            Milestones.Add(new MilestoneRowViewModel(_api, _logger, milestone));
        }
    }

    private void Apply(ProjectDto project)
    {
        // The swatch and lead setters below would otherwise read as user edits.
        _populating = true;

        ProjectName = project.Name;
        Summary = project.Summary ?? string.Empty;
        Description = project.Description ?? string.Empty;
        SelectedStatus = ProjectStatusOption.For(project.Status);
        SelectedHealth = ProjectHealthOption.For(project.Health);
        SelectedLead = Leads.FirstOrDefault(l => l.Member?.UserId == project.Lead?.Id) ?? LeadOption.None;
        ProjectColor = project.Color;
        StartDate = MilestoneRowViewModel.ToDateTime(project.StartDate);
        TargetDate = MilestoneRowViewModel.ToDateTime(project.TargetDate);

        _populating = false;

        Remember(project);
    }

    /// <summary>Snapshots the saved state the next diff is measured against.</summary>
    private void Remember(ProjectDto project)
    {
        _originalName = project.Name;
        _originalSummary = project.Summary;
        _originalDescription = project.Description;
        _originalStatus = project.Status;
        _originalHealth = project.Health;
        _originalColor = project.Color;
        _originalLeadId = project.Lead?.Id;
        _originalStartDate = project.StartDate;
        _originalTargetDate = project.TargetDate;

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(UnsavedSummary));
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    partial void OnProjectColorChanged(string? value)
    {
        foreach (var swatch in Swatches)
        {
            swatch.IsSelected = string.Equals(swatch.Value, value, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>The fields the Save button is responsible for. Touching any of them retires the
    /// "Saved." line, which would otherwise sit under a form that has since been edited and claim
    /// something about it that is no longer true.</summary>
    private static readonly HashSet<string> FormFields =
    [
        nameof(ProjectName), nameof(Summary), nameof(Description), nameof(SelectedStatus),
        nameof(SelectedHealth), nameof(SelectedLead), nameof(ProjectColor),
        nameof(StartDate), nameof(TargetDate), nameof(NewMilestoneName)
    ];

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        // Guarded against re-entry: raising HasUnsavedChanges and clearing Notice both land back here.
        if (e.PropertyName is not { } name || !FormFields.Contains(name))
        {
            return;
        }

        if (Notice is not null && !_populating)
        {
            Notice = null;
        }

        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    private void OnMilestoneRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MilestoneRowViewModel.IsDirty))
        {
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
    }

    [RelayCommand]
    private void PickColor(ColorSwatchViewModel? swatch)
    {
        if (swatch is not null)
        {
            ProjectColor = swatch.Value;
        }
    }

    [RelayCommand]
    private async Task SaveAsync(CancellationToken ct)
    {
        Error = null;
        Notice = null;

        var name = NameValue;

        if (string.IsNullOrEmpty(name))
        {
            Error = "Give the project a name.";
            return;
        }

        var start = MilestoneRowViewModel.ToDateOnly(StartDate);
        var target = MilestoneRowViewModel.ToDateOnly(TargetDate);

        if (start is { } from && target is { } to && to < from)
        {
            Error = "The target date cannot fall before the start date.";
            return;
        }

        IsBusy = true;

        try
        {
            var created = ProjectId is null;

            if (created || IsFormDirty)
            {
                var saved = ProjectId is { } projectId
                    ? await _api.UpdateProjectAsync(projectId, BuildPatch(name, start, target), ct)
                    : await _api.CreateProjectAsync(BuildCreate(name, start, target), ct);

                // A create is the moment this stops being a form and becomes the project's page: the id
                // arrives, the heading changes, and the milestone section has something to POST to.
                ProjectId = saved.Id;
                Remember(saved);

                if (created)
                {
                    OnPropertyChanged(nameof(IsEditing));
                    OnPropertyChanged(nameof(Subtitle));
                    OnPropertyChanged(nameof(SaveLabel));
                    NotifyMilestonesChanged();
                }

                _logger.LogInformation("{Action} project {Name}", created ? "Created" : "Updated", saved.Name);
                Saved?.Invoke(saved);
            }

            // The milestones are on this page, so they are part of saving it. Anything typed into the
            // add row counts too: a name sitting there when Save is pressed was meant to be added.
            await AddPendingMilestoneAsync(ct);
            var unsaved = await SaveMilestonesAsync(ct);

            if (unsaved > 0)
            {
                // Each row already says what went wrong with it; the page only says how many, so that
                // Save never reports success over work that is still sitting there.
                Error = unsaved == 1
                    ? "One milestone could not be saved — see the row for why."
                    : $"{unsaved} milestones could not be saved — see the rows for why.";
            }
            else
            {
                Notice = created ? "Project created." : "Saved.";
            }
        }
        catch (PlannerApiException ex)
        {
            // The API's problem detail is written for a person — "This team already has a project named
            // Apollo" beats anything this form could invent.
            Error = ex.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Could not save the project");
            Error = "Could not reach the server.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task AddMilestoneAsync(CancellationToken ct)
    {
        if (ProjectId is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(NewMilestoneName))
        {
            Error = "Give the milestone a name.";
            return;
        }

        Error = null;
        IsBusy = true;

        try
        {
            await AddPendingMilestoneAsync(ct);
        }
        catch (PlannerApiException ex)
        {
            Error = ex.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Could not add the milestone");
            Error = "Could not reach the server.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Adds whatever is sitting in the add row, and nothing if it is empty. Throws: the
    /// callers own the busy state and the error, because one of them is saving the whole page.</summary>
    private async Task AddPendingMilestoneAsync(CancellationToken ct)
    {
        var name = Trimmed(NewMilestoneName);

        if (ProjectId is not { } projectId || string.IsNullOrEmpty(name))
        {
            return;
        }

        var created = await _api.CreateMilestoneAsync(
            projectId,
            new CreateMilestoneRequest(
                name,
                null,
                MilestoneRowViewModel.ToDateOnly(NewMilestoneTargetDate),
                NewMilestoneStatus?.Value ?? MilestoneStatus.Upcoming),
            ct);

        Milestones.Add(new MilestoneRowViewModel(_api, _logger, created));

        NewMilestoneName = string.Empty;
        NewMilestoneTargetDate = null;
        NewMilestoneStatus = MilestoneStatusOption.All[0];

        _logger.LogInformation("Added milestone {Name}", created.Name);
    }

    /// <summary>Saves every row that differs from the server, and reports how many would not go. A row
    /// that fails keeps its own error and stays dirty, which is what the page counts.</summary>
    private async Task<int> SaveMilestonesAsync(CancellationToken ct)
    {
        var unsaved = 0;

        foreach (var row in Milestones.Where(m => m.IsDirty).ToList())
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
    private void Close() => Closed?.Invoke();

    private void OnMilestonesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var row in e.OldItems?.OfType<MilestoneRowViewModel>() ?? [])
        {
            Detach(row);
        }

        foreach (var row in e.NewItems?.OfType<MilestoneRowViewModel>() ?? [])
        {
            row.Deleted += OnMilestoneDeleted;
            row.PropertyChanged += OnMilestoneRowChanged;
        }

        NotifyMilestonesChanged();
    }

    private void Detach(MilestoneRowViewModel row)
    {
        row.Deleted -= OnMilestoneDeleted;
        row.PropertyChanged -= OnMilestoneRowChanged;
    }

    private void OnMilestoneDeleted(MilestoneRowViewModel row) => Milestones.Remove(row);

    private void NotifyMilestonesChanged()
    {
        OnPropertyChanged(nameof(HasMilestones));
        OnPropertyChanged(nameof(StatusSummary));
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    private CreateProjectRequest BuildCreate(string name, DateOnly? start, DateOnly? target) =>
        new(
            _teamId,
            name,
            Blank(Summary),
            Blank(Description),
            StatusValue,
            HealthValue,
            ColorValue,
            LeadId,
            start,
            target);

    /// <summary>Builds the smallest PATCH that expresses what the user changed. Anything untouched is
    /// left unset, which the serializer omits entirely — so the server never sees it and never
    /// overwrites it with a value this form happened to be holding.</summary>
    private UpdateProjectRequest BuildPatch(string name, DateOnly? start, DateOnly? target)
    {
        var summary = Blank(Summary);
        var description = Blank(Description);
        var color = ColorValue;
        var leadId = LeadId;

        return new UpdateProjectRequest(
            Name: When(name != _originalName, name),
            Summary: When(summary != _originalSummary, summary),
            Description: When(description != _originalDescription, description),
            Status: When(StatusValue != _originalStatus, StatusValue),
            Health: When(HealthValue != _originalHealth, HealthValue),
            Color: When(color != _originalColor, color),
            LeadUserId: When(leadId != _originalLeadId, leadId),
            StartDate: When(start != _originalStartDate, start),
            TargetDate: When(target != _originalTargetDate, target),
            SortOrder: default);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>A text box binding writes null as readily as it writes text — clearing the box does
    /// exactly that — so nothing here calls Trim on one directly.</summary>
    private static string Trimmed(string? value) => value?.Trim() ?? string.Empty;

    private static Optional<T> When<T>(bool changed, T value) => changed ? Optional<T>.From(value) : default;
}
