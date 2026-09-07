using Avalonia.Controls;
using Avalonia.Interactivity;
using Planner.Client.ViewModels;

namespace Planner.Client.Views;

/// <summary>The project page. Everything here is bindings except the one thing a binding cannot do:
/// deleting a milestone is confirmed in a popup, and AtomUI's confirm raises an event rather than
/// offering a command to bind the answer to.</summary>
public partial class ProjectEditorView : UserControl
{
    public ProjectEditorView() => InitializeComponent();

    private void OnDeleteMilestoneConfirmed(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: MilestoneRowViewModel row })
        {
            row.DeleteCommand.Execute(null);
        }
    }
}
