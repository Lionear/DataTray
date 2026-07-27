using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace DataTray.App.Converters;

/// <summary>Renders a 2px accent bar on one edge of a row when the bound bool is true, else empty
/// thickness. Two static singletons for the Top and Bottom edges keep the connection-manager row
/// XAML declarative (no code-behind style setters, no per-row bindings on individual edge fields).</summary>
public sealed class InsertLineThicknessConverter : IValueConverter
{
    public static readonly InsertLineThicknessConverter Top = new(new Thickness(0, 2, 0, 0));
    public static readonly InsertLineThicknessConverter Bottom = new(new Thickness(0, 0, 0, 2));

    private static readonly Thickness None = new(0);

    private readonly Thickness _active;

    private InsertLineThicknessConverter(Thickness active) => _active = active;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? _active : None;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
