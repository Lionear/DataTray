using System.Globalization;
using Avalonia.Data.Converters;

namespace DataTray.App.Converters;

/// <summary>One-way "does this value equal the ConverterParameter" check (string/enum-safe via
/// ToString()) — used to drive a RadioButton's IsChecked off a single backing property/enum.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public static readonly EqualsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
