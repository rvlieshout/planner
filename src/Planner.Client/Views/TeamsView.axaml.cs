using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Planner.Client.ViewModels;

namespace Planner.Client.Views;

/// <summary>The teams page. Everything here is bindings except the two things a binding cannot do:
/// AtomUI's confirm popup raises an event rather than offering a command to bind the answer to, and a
/// page whose writes are in flight has to stop taking input without dimming itself into
/// unreadability.</summary>
public partial class TeamsView : UserControl
{
    public TeamsView()
    {
        InitializeComponent();

        // Pointer input is blocked in XAML while a write is running; focused controls must also ignore
        // the keyboard, or a text box keeps taking edits the save has already read past.
        AddHandler(KeyDownEvent, BlockInputWhileBusy, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, BlockInputWhileBusy, RoutingStrategies.Tunnel);
    }

    private void BlockInputWhileBusy(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TeamsViewModel { IsBusy: true })
        {
            e.Handled = true;
        }
    }

    private void OnRemoveMemberConfirmed(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TeamMemberRowViewModel row })
        {
            row.RemoveCommand.Execute(null);
        }
    }

    private void OnArchiveConfirmed(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TeamsViewModel model)
        {
            model.ArchiveCommand.Execute(null);
        }
    }
}
