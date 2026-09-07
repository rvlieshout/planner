using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Planner.Client.ViewModels;

namespace Planner.Client.Views;

public partial class MyIssuesView : UserControl
{
    private readonly RowDrag _drag = new();

    /// <summary>The row the context menu was opened over, captured before the menu shows.</summary>
    private IssueCardViewModel? _contextRow;

    /// <summary>The group currently lit up as the drop target.</summary>
    private IssueGroupViewModel? _hovered;

    public MyIssuesView()
    {
        InitializeComponent();

        // Tunnelling, and on the view rather than on each list: ListBox marks PointerPressed handled
        // while it moves the selection, so a bubbling handler attached in XAML is never called and the
        // drag could never start.
        AddHandler(PointerPressedEvent, OnRowPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnRowMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnRowReleased, RoutingStrategies.Tunnel);
    }

    private void OnRowActivated(object? sender, TappedEventArgs e) => Open(IssueRows.From(e.Source));

    private void OnRowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter && sender is ListBox { SelectedItem: IssueCardViewModel card })
        {
            Open(card);
            e.Handled = true;
        }
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        _contextRow = IssueRows.From(e.Source);

        if (_contextRow is null)
        {
            e.Handled = true;
            return;
        }

        if (sender is ListBox list)
        {
            list.SelectedItem = _contextRow;
        }
    }

    private void OnOpenClick(object? sender, RoutedEventArgs e) => Open(_contextRow);

    private async void OnCopyKeyClick(object? sender, RoutedEventArgs e) =>
        await IssueRows.CopyKeyAsync(this, _contextRow);

    private void OnRowPressed(object? sender, PointerPressedEventArgs e) => _drag.Press(sender, e);

    private async void OnRowMoved(object? sender, PointerEventArgs e) => await _drag.MovedAsync(e);

    private void OnRowReleased(object? sender, PointerReleasedEventArgs e) => _drag.Clear();

    private void OnGroupDragOver(object? sender, DragEventArgs e)
    {
        var group = Group(sender);
        var card = IssueRows.Dragged(e);

        // Dropping a row back into the group it already sits in would change nothing.
        e.DragEffects = group is not null && card is not null && card.Issue.StateType != group.StateType
            ? DragDropEffects.Move
            : DragDropEffects.None;

        if (e.DragEffects is DragDropEffects.Move)
        {
            Highlight(group);
        }

        e.Handled = true;
    }

    private void OnGroupDragLeave(object? sender, DragEventArgs e)
    {
        if (ReferenceEquals(_hovered, Group(sender)))
        {
            Highlight(null);
        }
    }

    private async void OnGroupDrop(object? sender, DragEventArgs e)
    {
        Highlight(null);
        e.Handled = true;

        if (Group(sender) is { } group &&
            IssueRows.Dragged(e) is { } card &&
            DataContext is MyIssuesViewModel issues)
        {
            await issues.MoveAsync(card, group);
        }
    }

    private static IssueGroupViewModel? Group(object? sender) =>
        (sender as Control)?.DataContext as IssueGroupViewModel;

    private void Highlight(IssueGroupViewModel? group)
    {
        if (ReferenceEquals(_hovered, group))
        {
            return;
        }

        if (_hovered is not null)
        {
            _hovered.IsDropTarget = false;
        }

        _hovered = group;

        if (_hovered is not null)
        {
            _hovered.IsDropTarget = true;
        }
    }

    private void Open(IssueCardViewModel? card)
    {
        if (card is not null && DataContext is MyIssuesViewModel issues)
        {
            issues.OpenIssueCommand.Execute(card);
        }
    }
}
