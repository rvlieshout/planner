using Avalonia.Controls;

namespace Planner.Client.Views;

/// <summary>The issue form as a modal dialog. The workspace view owns its lifetime: it opens the
/// window when the view model produces an editor and closes it when that editor goes away.</summary>
public partial class IssueEditorWindow : Window
{
    public IssueEditorWindow() => InitializeComponent();
}
