using System.Globalization;
using Avalonia.Data.Converters;

namespace DataTray.App.Converters;

/// <summary>One-way upper-casing for the Preferences rail's group labels and the numbered section headings
/// (SE-290). The casing is presentation, not content, so it happens here rather than in the resx — the same
/// string still reads normally anywhere else it is used, and a translator never has to shout.</summary>
public sealed class UpperCaseConverter : IValueConverter
{
    public static readonly UpperCaseConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text ? text.ToUpper(culture) : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
