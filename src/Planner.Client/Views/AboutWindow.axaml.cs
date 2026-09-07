using AtomUI.Desktop.Controls;
using Avalonia.Interactivity;

namespace Planner.Client.Views;

public partial class AboutWindow : Window
{
    public AboutWindow() => InitializeComponent();

    // IsDefault and IsCancel both route here, so Enter and Escape dismiss the dialog.
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
