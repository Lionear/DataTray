using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace DataTray.Tools.ErDiagram;

/// <summary>
/// Writes the diagram to a file (SE-226). Both formats read the <i>same</i> measured boxes the canvas
/// draws from, so the export cannot drift from the picture on screen — which it would the moment two
/// copies of the geometry existed.
/// </summary>
public static class ErDiagramExport
{
    public const string Png = "png";
    public const string Svg = "svg";

    /// <summary>Renders the control exactly as it appears, at 2× for a usable result when it is pasted
    /// into a document and then scaled.</summary>
    public static void WritePng(ErDiagramCanvas canvas, string path, double scale = 2.0)
    {
        var size = canvas.DiagramSize;
        var pixel = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(size.Width * scale)),
            Math.Max(1, (int)Math.Ceiling(size.Height * scale)));

        using var bitmap = new RenderTargetBitmap(pixel, new Vector(96 * scale, 96 * scale));
        canvas.Measure(size);
        canvas.Arrange(new Rect(size));
        bitmap.Render(canvas);
        bitmap.Save(path);
    }

    /// <summary>
    /// Writes the same geometry as SVG. Hand-rolled rather than rendered: the point of a vector export is
    /// text that stays text — a table name you can search for in the file, and lines that stay crisp at
    /// any zoom — which a rasterised trace would lose.
    /// </summary>
    public static void WriteSvg(ErDiagramCanvas canvas, string path)
    {
        var svg = BuildSvg(canvas);
        File.WriteAllText(path, svg, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    internal static string BuildSvg(ErDiagramCanvas canvas)
    {
        var size = canvas.DiagramSize;
        var palette = canvas.Palette;
        var sb = new StringBuilder();

        sb.Append(CultureInfo.InvariantCulture,
            $"""
             <?xml version="1.0" encoding="UTF-8"?>
             <svg xmlns="http://www.w3.org/2000/svg" width="{N(size.Width)}" height="{N(size.Height)}" viewBox="0 0 {N(size.Width)} {N(size.Height)}">
               <rect width="100%" height="100%" fill="{Hex(palette.Canvas)}"/>

             """);

        foreach (var line in canvas.RelationSegments())
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"""  <line x1="{N(line.A.X)}" y1="{N(line.A.Y)}" x2="{N(line.B.X)}" y2="{N(line.B.Y)}" stroke="{Hex(palette.Relation)}" stroke-width="1.2"/>{"\n"}""");
        }

        foreach (var box in canvas.TableShapes())
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"""
                   <g>
                     <rect x="{N(box.Bounds.X)}" y="{N(box.Bounds.Y)}" width="{N(box.Bounds.Width)}" height="{N(box.Bounds.Height)}" rx="6" fill="{Hex(palette.Box)}" stroke="{Hex(palette.Hairline)}"/>
                     <path d="M{N(box.Bounds.X)},{N(box.Bounds.Y + 6)} a6,6 0 0 1 6,-6 h{N(box.Bounds.Width - 12)} a6,6 0 0 1 6,6 v{N(ErDiagramCanvas.HeaderHeightPx - 6)} h-{N(box.Bounds.Width)} z" fill="{Hex(palette.Header)}"/>
                     <text x="{N(box.Bounds.X + 10)}" y="{N(box.Bounds.Y + 18)}" font-family="sans-serif" font-size="12.5" font-weight="600" fill="{Hex(palette.Text)}">{Escape(box.Name)}</text>

                 """);

            foreach (var row in box.Rows)
            {
                if (row.Badge.Length > 0)
                {
                    var badgeColour = row.Badge == "PK" ? palette.KeyPrimary : palette.KeyForeign;
                    sb.Append(CultureInfo.InvariantCulture,
                        $"""    <text x="{N(box.Bounds.X + 10)}" y="{N(row.Y + 10)}" font-family="sans-serif" font-size="9.5" font-weight="700" fill="{Hex(badgeColour)}">{row.Badge}</text>{"\n"}""");
                }

                sb.Append(CultureInfo.InvariantCulture,
                    $"""    <text x="{N(box.Bounds.X + 34)}" y="{N(row.Y + 11)}" font-family="sans-serif" font-size="11" fill="{Hex(palette.Text)}">{Escape(row.Column)}</text>{"\n"}""");
                sb.Append(CultureInfo.InvariantCulture,
                    $"""    <text x="{N(box.Bounds.Right - 10)}" y="{N(row.Y + 11)}" text-anchor="end" font-family="sans-serif" font-size="10" fill="{Hex(palette.TextFaint)}">{Escape(row.DataType)}</text>{"\n"}""");
            }

            sb.Append("  </g>\n");
        }

        sb.Append("</svg>\n");
        return sb.ToString();
    }

    private static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Hex(IBrush brush) =>
        brush is ISolidColorBrush s ? $"#{s.Color.R:X2}{s.Color.G:X2}{s.Color.B:X2}" : "#000000";

    /// <summary>Table and column names come from a database and can contain anything, including the five
    /// characters that would otherwise make the SVG invalid.</summary>
    private static string Escape(string text) => text
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&apos;");
}
