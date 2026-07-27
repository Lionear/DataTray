using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace DataTray.App.Converters;

/// <summary>Fills the history row's star when the query is a favorite; an unstarred one stays an
/// outline, so the two states differ in shape rather than only in shade (SE-31).</summary>
public sealed class FavoriteStarFillConverter : IValueConverter
{
    public static readonly FavoriteStarFillConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Brushes.Goldenrod : Brushes.Transparent;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
