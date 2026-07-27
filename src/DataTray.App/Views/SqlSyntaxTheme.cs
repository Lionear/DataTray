using Avalonia.Media;
using AvaloniaEdit.Highlighting;

namespace DataTray.App.Views;

/// <summary>
/// Makes AvaloniaEdit's built-in syntax highlighting readable in dark mode. The stock TSQL definition
/// colours keywords a dark blue that vanishes against a dark editor background, so on the dark theme we
/// lighten any dark foreground toward white (keeping its hue); on the light theme the originals are
/// restored. The definition is shared process-wide, so the original colours are captured once and every
/// re-apply starts from them (no compounding).
/// </summary>
public static class SqlSyntaxTheme
{
    private static readonly Dictionary<HighlightingColor, HighlightingBrush?> Originals = new();

    public static void Apply(IHighlightingDefinition definition, bool dark)
    {
        foreach (var color in definition.NamedHighlightingColors)
        {
            if (!Originals.TryGetValue(color, out var original))
            {
                original = color.Foreground;
                Originals[color] = original;
            }

            if (original is null)
            {
                continue;
            }

            if (!dark)
            {
                color.Foreground = original;
            }
            else if (original.GetColor(null) is { } baseColor)
            {
                color.Foreground = new SimpleHighlightingBrush(Brighten(baseColor));
            }
        }
    }

    // Lighten a dark colour toward white so it reads on a dark background; leave already-light ones alone.
    private static Color Brighten(Color c)
    {
        var luminance = (0.299 * c.R) + (0.587 * c.G) + (0.114 * c.B);
        if (luminance >= 150)
        {
            return c;
        }

        static byte Lift(byte v) => (byte)(v + ((255 - v) * 0.55));
        return Color.FromRgb(Lift(c.R), Lift(c.G), Lift(c.B));
    }
}
