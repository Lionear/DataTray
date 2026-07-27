using System.Globalization;
using Avalonia.Data.Converters;

namespace DataTray.App.Converters;

/// <summary>Renders a SQL NULL cell as the literal text "NULL" so it is visually distinct from an empty
/// string (which stays blank). Non-null values pass through unchanged.</summary>
public sealed class NullCellTextConverter : IValueConverter
{
    public static readonly NullCellTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? "NULL" : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Fades a NULL cell (paired with <see cref="NullCellTextConverter"/>) so the "NULL" marker reads
/// as metadata rather than a real value.</summary>
public sealed class NullCellOpacityConverter : IValueConverter
{
    public static readonly NullCellOpacityConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? 0.45 : 1.0;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
