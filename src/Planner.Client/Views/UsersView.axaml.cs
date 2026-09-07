using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Planner.Client.ViewModels;

namespace Planner.Client.Views;

public partial class UsersView : UserControl
{
    public UsersView()
    {
        InitializeComponent();
        // Block edits without disabling (and dimming) the whole page. Pointer input is
        // blocked in XAML; focused controls must also ignore keyboard and text input.
        AddHandler(KeyDownEvent, BlockInputWhileLoading, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, BlockInputWhileLoading, RoutingStrategies.Tunnel);
    }

    private void BlockInputWhileLoading(object? sender, RoutedEventArgs e)
    {
        if (DataContext is UsersViewModel { IsLoading: true }) e.Handled = true;
    }
}
