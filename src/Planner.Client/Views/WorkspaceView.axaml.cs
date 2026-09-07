using System.ComponentModel;
using Avalonia.Controls;
using Planner.Client.ViewModels;

namespace Planner.Client.Views;

/// <summary>Hosts the two pieces of window behaviour a view model cannot express on its own: the
/// collapsible sidebar column, and the issue editor as a real modal dialog rather than a panel drawn
/// over the page.</summary>
public partial class WorkspaceView : UserControl
{
    private const double DefaultSidebarWidth = 216;
    private const double MinimumSidebarWidth = 150;

    private double _sidebarWidth = DefaultSidebarWidth;
    private WorkspaceViewModel? _model;
    private IssueEditorWindow? _dialog;

    public WorkspaceView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelPropertyChanged;
        }

        _model = DataContext as WorkspaceViewModel;

        if (_model is not null)
        {
            _model.PropertyChanged += OnModelPropertyChanged;
        }

        ApplySidebar();
        SyncEditor();
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(WorkspaceViewModel.IsSidebarVisible):
                ApplySidebar();
                break;
            case nameof(WorkspaceViewModel.Editor):
                SyncEditor();
                break;
        }
    }

    private void ApplySidebar()
    {
        var column = RootGrid.ColumnDefinitions[0];

        if (_model?.IsSidebarVisible ?? true)
        {
            column.MinWidth = MinimumSidebarWidth;
            column.Width = new GridLength(_sidebarWidth);
            return;
        }

        // Remember whatever the splitter was last left at, then let the column close completely —
        // MinWidth would otherwise hold it open at 150px.
        if (column.Width.IsAbsolute && column.Width.Value > 0)
        {
            _sidebarWidth = column.Width.Value;
        }

        column.MinWidth = 0;
        column.Width = new GridLength(0);
    }

    private void SyncEditor()
    {
        if (_model?.Editor is not { } editor)
        {
            // Detach first: the Closed handler below must not read this as the user dismissing it.
            if (_dialog is { } open)
            {
                _dialog = null;
                open.Close();
            }

            return;
        }

        if (_dialog is not null || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var dialog = new IssueEditorWindow { DataContext = editor };
        _dialog = dialog;

        dialog.Closed += (_, _) =>
        {
            // Closing the window from its own title bar has to reach the view model, or the workspace
            // would still believe the editor is open and refuse to open another.
            if (ReferenceEquals(_dialog, dialog))
            {
                _dialog = null;
                _model?.CloseEditorCommand.Execute(null);
            }
        };

        _ = dialog.ShowDialog(owner);
    }
}
