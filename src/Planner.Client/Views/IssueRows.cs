using Avalonia;
using Avalonia.Controls;
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
    /// first row whose middle is below the pointer, or the end of the list.</summary>
    public static int DropIndex(ListBox list, DragEventArgs e)
    {
        var y = e.GetPosition(list).Y;
        var index = list.ItemCount;

        foreach (var container in list.GetRealizedContainers())
        {
            var top = container.TranslatePoint(default, list)?.Y;
            if (top is not { } offset || y >= offset + container.Bounds.Height / 2)
            {
                continue;
            }

            index = Math.Min(index, list.IndexFromContainer(container));
        }

        return index;
    }

    public static async Task CopyKeyAsync(Visual view, IssueCardViewModel? card)
    {
        if (card is not null && TopLevel.GetTopLevel(view)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(card.Key);
        }
    }
}

/// <summary>Turns press-then-move on a row into a drag.
///
/// The press is only remembered, never handled, so clicking still selects and double-clicking still
/// opens; the drag starts once the pointer has travelled far enough to mean it. Avalonia wants the
/// originating <see cref="PointerPressedEventArgs"/> to start a drag, which is the reason this holds
/// on to it rather than to a position alone.</summary>
internal sealed class RowDrag
{
    private const double Threshold = 4;

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

    public void Clear()
    {
        _press = null;
        _card = null;
        _dragging = false;
    }

    private static IssueCardViewModel? From(object? source, PointerPressedEventArgs e) =>
        IssueRows.From(e.Source) ?? IssueRows.From(source);
}
