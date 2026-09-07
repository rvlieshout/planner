using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Planner.Client.Controls;

/// <summary>Draws a 24×24 Lucide stroke icon at an arbitrary size.
///
/// A <see cref="Path"/> with <c>Stretch</c> would scale the outline but leave the stroke width in the
/// stretched space, so a 14px icon and a 20px icon end up with visually different weights. Rendering
/// directly means the stroke scales with the glyph, exactly as the SVG does.
///
/// <see cref="Foreground"/> defaults to the inherited text colour, so an icon next to a label picks up
/// that label's colour without being told.</summary>
public sealed class Icon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(Size), 16d);

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<Icon>();

    /// <summary>Lucide's own stroke width, in the icon's 24-unit space.</summary>
    public static readonly StyledProperty<double> StrokeWidthProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(StrokeWidth), 2d);

    private const double DesignSize = 24d;

    static Icon()
    {
        AffectsRender<Icon>(DataProperty, ForegroundProperty, StrokeWidthProperty);
        AffectsMeasure<Icon>(SizeProperty);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double StrokeWidth
    {
        get => GetValue(StrokeWidthProperty);
        set => SetValue(StrokeWidthProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        if (Data is not { } geometry || Foreground is not { } brush || Size <= 0)
        {
            return;
        }

        var scale = Size / DesignSize;

        // Round caps and joins are what give Lucide its look; without them the short strokes in icons
        // like signal-high read as clipped rectangles.
        var pen = new Pen(brush, StrokeWidth)
        {
            LineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };

        using var _ = context.PushTransform(Matrix.CreateScale(scale, scale));
        context.DrawGeometry(null, pen, geometry);
    }
}
