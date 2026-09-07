using AtomUI.Desktop.Controls;
using Avalonia.Interactivity;

namespace Planner.Client.Views;

/// <summary>A yes-or-no question, as a real modal dialog.
///
/// Deliberately not view-model shaped: it has no state worth binding, it is opened from code that
/// already has the words to put in it, and giving it a view model would mean a second file to say
/// "these two strings and two buttons".</summary>
public partial class ConfirmWindow : Window
{
    public ConfirmWindow() => InitializeComponent();

    /// <summary>Shows the question and answers it. Closing the dialog by any other route — the title
    /// bar, Escape — answers no, because the destructive button is never the one a stray keystroke
    /// reaches.</summary>
    public static Task<bool> AskAsync(
        Avalonia.Controls.Window owner, string headline, string detail, string confirmText, string cancelText)
    {
        var dialog = new ConfirmWindow { Title = headline };

        dialog.Headline.Text = headline;
        dialog.Detail.Text = detail;
        dialog.ConfirmButton.Content = confirmText;
        dialog.CancelButton.Content = cancelText;

        return dialog.ShowDialog<bool>(owner);
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
