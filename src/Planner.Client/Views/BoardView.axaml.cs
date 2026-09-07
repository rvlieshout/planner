using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Planner.Client.ViewModels;

namespace Planner.Client.Views;

public partial class BoardView : UserControl
{
    private readonly RowDrag _drag = new();

    /// <summary>The row the context menu was opened over, captured before the menu shows.</summary>
    private IssueCardViewModel? _contextRow;

    /// <summary>The column currently lit up as the drop target. Held so it can be cleared when the
    /// drag moves on: DragLeave for the old column arrives after DragEnter for the new one.</summary>
    private BoardColumnViewModel? _hovered;

    public BoardView()
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
            // Right-clicking the empty part of a column has nothing to act on.
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

    private void OnColumnDragOver(object? sender, DragEventArgs e)
    {
        var column = Column(sender);

        // A drag carrying anything else — a file, text from another app — is not a move of ours.
        e.DragEffects = column is not null && IssueRows.Dragged(e) is not null
            ? DragDropEffects.Move
            : DragDropEffects.None;

        if (e.DragEffects is DragDropEffects.Move)
        {
            Highlight(column);
        }

        e.Handled = true;
    }

    private void OnColumnDragLeave(object? sender, DragEventArgs e)
    {
        if (ReferenceEquals(_hovered, Column(sender)))
        {
            Highlight(null);
        }
    }

    private async void OnColumnDrop(object? sender, DragEventArgs e)
    {
        Highlight(null);
        e.Handled = true;

        if (sender is not ListBox list ||
            Column(sender) is not { } column ||
            IssueRows.Dragged(e) is not { } card ||
            DataContext is not BoardViewModel board)
        {
            return;
        }

        await board.MoveAsync(card, column, IssueRows.DropIndex(list, e));
    }

    private static BoardColumnViewModel? Column(object? sender) =>
        (sender as Control)?.DataContext as BoardColumnViewModel;

    private void Highlight(BoardColumnViewModel? column)
    {
        if (ReferenceEquals(_hovered, column))
        {
            return;
        }

        if (_hovered is not null)
        {
            _hovered.IsDropTarget = false;
        }

        _hovered = column;

        if (_hovered is not null)
        {
            _hovered.IsDropTarget = true;
        }
    }

    private void Open(IssueCardViewModel? card)
    {
        if (card is not null && DataContext is BoardViewModel board)
        {
            board.OpenIssueCommand.Execute(card);
        }
    }
}
