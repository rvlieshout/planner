using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Planner.Client.Controls;
using Planner.Contracts.Issues;
using Planner.Contracts.Realtime;

namespace Planner.Client.ViewModels;

public enum NavKind
{
    MyIssues,
    Board,
    Project
}

/// <summary>One row in the left-hand navigation.</summary>
public sealed partial class NavItemViewModel : ViewModelBase
{
    private NavItemViewModel(NavKind kind, string title, Geometry? icon)
    {
        Kind = kind;
        Title = title;
        Icon = icon;
    }

    public NavKind Kind { get; }

    public string Title { get; }

    public Geometry? Icon { get; }

    /// <summary>Set for <see cref="NavKind.Project"/>. The dot next to a project uses its own colour,
    /// which is how someone recognises it in the list without reading the name.</summary>
    public string? AccentColor { get; private init; }

    public Guid? ProjectId { get; private init; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Issue count, shown right-aligned. Null until something has counted.</summary>
    [ObservableProperty]
    public partial int? Count { get; set; }

    public bool ShowDot => AccentColor is not null;

    public static NavItemViewModel MyIssues() =>
        new(NavKind.MyIssues, "My Issues", AppIcons.MyIssues);

    public static NavItemViewModel Board(string teamName) =>
        new(NavKind.Board, teamName, AppIcons.Board);

    public static NavItemViewModel Project(Guid id, string name, string color) =>
        new(NavKind.Project, name, null) { ProjectId = id, AccentColor = color };
}

/// <summary>What the main pane can show. Both implementations take live changes rather than refetching,
/// so the view the user is looking at is the one that updates.</summary>
public interface IWorkspaceContent
{
    string Title { get; }

    string? Subtitle { get; }

    /// <summary>What the status bar says about this view — how much is in it. Raises
    /// <see cref="System.ComponentModel.INotifyPropertyChanged"/> as the contents change.</summary>
    string StatusSummary { get; }

    bool IsLoading { get; }

    /// <summary>Raised when the user clicks an issue. The workspace opens the editor; the content view
    /// does not need to know that, which keeps each view bound only to its own view model.</summary>
    event Action<IssueCardViewModel>? IssueActivated;

    Task LoadAsync(CancellationToken ct);

    void ApplyIssueChange(EntityChange<IssueSummary> change);
}
