using System.Globalization;
using Avalonia.Data.Converters;

namespace DataTray.App.Converters;

/// <summary>Formats the first value (a localized format string) with the rest, so the text follows a language switch.</summary>
public sealed class StringFormatConverter : IMultiValueConverter
{
    public static readonly StringFormatConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Count > 0 && values[0] is string format
            ? string.Format(culture, format, values.Skip(1).ToArray())
            : null;
}
