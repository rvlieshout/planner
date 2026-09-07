using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Planner.Client.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // The maximise button shows what it will do next, so the glyph follows the state.
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
            {
                ShowStateGlyph();
            }
        };

        ShowStateGlyph();
    }

    /// <summary>Help ▸ About. A modal owned by this window, which is what makes it a dialog rather
    /// than a second application window in the taskbar.</summary>
    private async void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        var about = new AboutWindow { DataContext = DataContext };
        await about.ShowDialog(this);
    }

    /// <summary>The window is dragged and maximised by its title bar like any other, except that this
    /// title bar is our own content: the system one is gone, so nothing else is left to do it. Buttons
    /// and the menu mark the press handled, so only the bare strip drags.</summary>
    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximised();
            e.Handled = true;
            return;
        }

        BeginMoveDrag(e);
    }

    private void OnMinimiseClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximiseClick(object? sender, RoutedEventArgs e) => ToggleMaximised();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximised() =>
        WindowState = WindowState is WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void ShowStateGlyph()
    {
        var maximised = WindowState is WindowState.Maximized;

        MaximiseGlyph.IsVisible = !maximised;
        RestoreGlyph.IsVisible = maximised;
    }
}
