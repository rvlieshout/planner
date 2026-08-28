using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Planner.Client.Converters;

/// <summary>Turns the hex colours the API stores ("#6E79F1") into brushes.
///
/// Relying on Avalonia's implicit string-to-brush conversion would work until a team picked a colour
/// the parser dislikes, and then the element would silently fail to paint. Doing it here means a bad
/// value falls back to something visible instead.</summary>
public sealed class HexBrushConverter : IValueConverter
{
    public static readonly HexBrushConverter Instance = new();

    private static readonly IBrush Fallback = new SolidColorBrush(Color.FromRgb(0x95, 0xA2, 0xB3));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string hex || string.IsNullOrWhiteSpace(hex))
        {
            return Fallback;
        }

        return Color.TryParse(hex, out var color) ? new SolidColorBrush(color) : Fallback;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
