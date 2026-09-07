using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Planner.Client.ViewModels;

namespace Planner.Client.Views;

public partial class BoardView : UserControl
{
    private readonly RowDrag _drag;

    /// <summary>The row the context menu was opened over, captured before the menu shows.</summary>
    private IssueCardViewModel? _contextRow;

    /// <summary>The column currently lit up as the drop target. Held so it can be cleared when the
    /// drag moves on: DragLeave for the old column arrives after DragEnter for the new one.</summary>
    private BoardColumnViewModel? _hovered;

    public BoardView()
    {
        InitializeComponent();

        _drag = new RowDrag(this);

        // Tunnelling, and on the view rather than on each list: ListBox marks PointerPressed handled
        // while it moves the selection, so a bubbling handler attached in XAML is never called and the
        // drag could never start.
        AddHandler(PointerPressedEvent, OnRowPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnRowMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnRowReleased, RoutingStrategies.Tunnel);

        // The ghost has to keep up with the cursor everywhere on the board, including the gaps between
        // columns where no column will handle the event. Tunnelling reaches this view before the
        // column that will mark the event handled, and the view itself allows the drop so that the
        // empty space still raises one.
        AddHandler(DragDrop.DragOverEvent, OnDragOverPreview, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DropEvent, OnDragFinished, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeftBoard);
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

    private void OnDragOverPreview(object? sender, DragEventArgs e) => _drag.Track(e.GetPosition(this));

    private void OnDragFinished(object? sender, DragEventArgs e) => _drag.Clear();

    /// <summary>The pointer has left the board altogether — another view, or outside the window. Nothing
    /// here would take the drop, so nothing here should still be claiming it would.</summary>
    private void OnDragLeftBoard(object? sender, DragEventArgs e) => Highlight(null, 0);

    private void OnColumnDragOver(object? sender, DragEventArgs e)
    {
        var column = Column(sender);

        // A drag carrying anything else — a file, text from another app — is not a move of ours.
        e.DragEffects = column is not null && IssueRows.Dragged(e) is not null
            ? DragDropEffects.Move
            : DragDropEffects.None;

        if (e.DragEffects is DragDropEffects.Move && sender is ListBox list)
        {
            // The same call the drop itself will make, so what is drawn is what will happen.
            Highlight(column, IssueRows.DropTarget(list, e).Offset);
        }

        e.Handled = true;
    }

    private void OnColumnDragLeave(object? sender, DragEventArgs e)
    {
        if (ReferenceEquals(_hovered, Column(sender)))
        {
            Highlight(null, 0);
        }
    }

    private async void OnColumnDrop(object? sender, DragEventArgs e)
    {
        Highlight(null, 0);
        e.Handled = true;

        if (sender is not ListBox list ||
            Column(sender) is not { } column ||
            IssueRows.Dragged(e) is not { } card ||
            DataContext is not BoardViewModel board)
        {
            return;
        }

        await board.MoveAsync(card, column, IssueRows.DropTarget(list, e).Index);
    }

    private static BoardColumnViewModel? Column(object? sender) =>
        (sender as Control)?.DataContext as BoardColumnViewModel;

    /// <summary>Points the whole drop indication — the lit column and the rule inside it — at one place,
    /// or at nowhere.</summary>
    private void Highlight(BoardColumnViewModel? column, double offset)
    {
        if (!ReferenceEquals(_hovered, column) && _hovered is not null)
        {
            _hovered.IsDropTarget = false;
        }

        _hovered = column;

        if (_hovered is not null)
        {
            _hovered.DropIndicatorOffset = offset;
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
