using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace DataTray.App.Converters;

/// <summary>
/// Maps an output-entry <c>IsError</c> flag to its badge colour: red for errors, muted green for
/// ordinary "OK" notices. Shares the red with the connection-state error dot.
/// </summary>
public sealed class OutputLevelBrushConverter : IValueConverter
{
    public static readonly OutputLevelBrushConverter Instance = new();

    private static readonly IImmutableBrush Error = new ImmutableSolidColorBrush(Color.FromRgb(0xD6, 0x45, 0x45));
    private static readonly IImmutableBrush Info = new ImmutableSolidColorBrush(Color.FromRgb(0x5A, 0xA5, 0x76));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Error : Info;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
