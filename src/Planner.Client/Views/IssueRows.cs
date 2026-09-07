using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using Planner.Client.ViewModels;

namespace Planner.Client.Views;

/// <summary>The list conventions both issue views follow: a single click selects, a double click or
/// Enter opens, the context menu acts on the row it was opened over, and a row can be dragged
/// somewhere else.</summary>
internal static class IssueRows
{
    /// <summary>The drag payload. In-process: the two views live in one window, so the issue travels
    /// as the object itself rather than as a serialised id that would have to be looked up again.</summary>
    public static readonly DataFormat<IssueCardViewModel> Format =
        DataFormat.CreateInProcessFormat<IssueCardViewModel>("planner.issue");

    /// <summary>The issue under an event's source element, or null if the event came from the empty
    /// space below the rows.</summary>
    public static IssueCardViewModel? From(object? source) =>
        (source as Visual)?
            .GetSelfAndVisualAncestors()
            .OfType<ListBoxItem>()
            .FirstOrDefault()?
            .DataContext as IssueCardViewModel;

    public static IssueCardViewModel? Dragged(DragEventArgs e) => e.DataTransfer.TryGetValue(Format);

    /// <summary>Where in <paramref name="list"/> a drop at this position belongs: the index of the
    /// first row whose middle is below the pointer, and the offset down the list at which that
    /// boundary sits.
    ///
    /// The index is what the move is made with, the offset is what the user is shown, and both come out
    /// of the same walk — so the rule can never point at a different gap from the one that is used.</summary>
    public static (int Index, double Offset) DropTarget(ListBox list, DragEventArgs e)
    {
        var y = e.GetPosition(list).Y;

        var index = list.ItemCount;
        double? boundary = null;

        // Where the rule goes when the drop lands past the last row — or at the top of an empty column,
        // which is the only case where no container is walked at all.
        var end = list.Padding.Top;

        // Realised containers arrive in no particular order, so the lowest matching index wins rather
        // than the first one seen, and the offset is only read off the container that won.
        foreach (var container in list.GetRealizedContainers())
        {
            if (container.TranslatePoint(default, list)?.Y is not { } top)
            {
                continue;
            }

            end = Math.Max(end, top + container.Bounds.Height);

            if (y < top + container.Bounds.Height / 2 &&
                list.IndexFromContainer(container) is var candidate && candidate < index)
            {
                index = candidate;
                boundary = top;
            }
        }

        return (index, boundary ?? end);
    }

    public static async Task CopyKeyAsync(Visual view, IssueCardViewModel? card)
    {
        if (card is not null && TopLevel.GetTopLevel(view)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(card.Key);
        }
    }
}

/// <summary>The card that follows the cursor while an issue is being dragged.
///
/// A platform drag owns the cursor, so the only thing an app can put underneath it is something it
/// draws itself. This lives in the window overlay layer — above every view, outside every clip — and is
/// moved from the drag events the views already receive.
///
/// Without it a drag is close to invisible: the source row dims, a column lights up, and nothing in
/// between says what is in flight.</summary>
internal sealed class DragGhost
{
    /// <summary>Clear of the cursor, and below-right of it, so the pointer keeps pointing at the gap the
    /// drop would use rather than at the card being carried.</summary>
    private static readonly Point CursorOffset = new(14, 12);

    private OverlayLayer? _layer;
    private Control? _visual;

    public void Show(Visual anchor, IssueCardViewModel card)
    {
        Hide();

        _layer = OverlayLayer.GetOverlayLayer(anchor);

        if (_layer is null)
        {
            return;
        }

        _visual = Build(card);
        _layer.Children.Add(_visual);
    }

    /// <summary>Moves the ghost to a position given in <paramref name="source"/>'s coordinates.</summary>
    public void MoveTo(Visual source, Point position)
    {
        if (_layer is null || _visual is null)
        {
            return;
        }

        if (source.TranslatePoint(position, _layer) is not { } inLayer)
        {
            return;
        }

        Canvas.SetLeft(_visual, inLayer.X + CursorOffset.X);
        Canvas.SetTop(_visual, inLayer.Y + CursorOffset.Y);
    }

    public void Hide()
    {
        if (_layer is not null && _visual is not null)
        {
            _layer.Children.Remove(_visual);
        }

        _layer = null;
        _visual = null;
    }

    /// <summary>The ghost is a view like any other, so what it looks like lives in XAML with the rest
    /// of the card visuals rather than being rebuilt out of brushes here.</summary>
    private static Control Build(IssueCardViewModel card) => new DragGhostView { DataContext = card };
}

/// <summary>Turns press-then-move on a row into a drag.
///
/// The press is only remembered, never handled, so clicking still selects and double-clicking still
/// opens; the drag starts once the pointer has travelled far enough to mean it. Avalonia wants the
/// originating <see cref="PointerPressedEventArgs"/> to start a drag, which is the reason this holds
/// on to it rather than to a position alone.
///
/// It also owns what the drag looks like: the source row dims and a ghost picks up the cursor for
/// exactly as long as the platform drag runs.</summary>
internal sealed class RowDrag(Visual owner)
{
    private const double Threshold = 4;

    private readonly DragGhost _ghost = new();

    private PointerPressedEventArgs? _press;
    private Point _origin;
    private IssueCardViewModel? _card;
    private bool _dragging;

    public void Press(object? source, PointerPressedEventArgs e)
    {
        Clear();

        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (From(source, e) is { } card)
        {
            _press = e;

            // Window coordinates: the row the pointer started on may well be scrolled or re-laid-out
            // by the time the drag threshold is crossed.
            _origin = e.GetPosition(null);
            _card = card;
        }
    }

    public async Task MovedAsync(PointerEventArgs e)
    {
        if (_dragging || _press is not { } press || _card is not { } card)
        {
            return;
        }

        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
        {
            Clear();
            return;
        }

        var moved = e.GetPosition(null) - _origin;
        if (Math.Abs(moved.X) < Threshold && Math.Abs(moved.Y) < Threshold)
        {
            return;
        }

        _dragging = true;

        // The two halves of "this issue is in your hand": the row it came from steps back, and a copy
        // of it picks up the cursor.
        card.IsDragging = true;
        _ghost.Show(owner, card);
        _ghost.MoveTo(owner, e.GetPosition(owner));

        using var payload = new DataTransfer();
        payload.Add(DataTransferItem.Create(IssueRows.Format, card));

        try
        {
            await DragDrop.DoDragDropAsync(press, payload, DragDropEffects.Move);
        }
        finally
        {
            Clear();
        }
    }

    /// <summary>Moves the ghost. Called from the drag events the view receives, in that view's own
    /// coordinates. A no-op unless a drag is actually running.</summary>
    public void Track(Point position) => _ghost.MoveTo(owner, position);

    public void Clear()
    {
        _ghost.Hide();

        if (_card is not null)
        {
            _card.IsDragging = false;
        }

        _press = null;
        _card = null;
        _dragging = false;
    }

    private static IssueCardViewModel? From(object? source, PointerPressedEventArgs e) =>
        IssueRows.From(e.Source) ?? IssueRows.From(source);
}
