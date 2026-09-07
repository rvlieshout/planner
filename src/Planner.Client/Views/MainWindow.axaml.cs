using AtomUI.Desktop.Controls;
using Avalonia.Interactivity;

namespace Planner.Client.Views;

/// <summary>The application window.
///
/// It derives from AtomUI's Window rather than Avalonia's, which is what puts the caption strip under
/// the app's own control: the title bar, its buttons, the drag and double-click behaviour and the
/// per-platform differences between them all come from the control theme. The window itself only has to
/// say what else belongs up there, which it does in XAML.</summary>
public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    /// <summary>Help ▸ About. A modal owned by this window, which is what makes it a dialog rather
    /// than a second application window in the taskbar.</summary>
    private async void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        var about = new AboutWindow { DataContext = DataContext };
        await about.ShowDialog(this);
    }
}
