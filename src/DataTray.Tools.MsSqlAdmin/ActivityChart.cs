using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace DataTray.Tools.MsSqlAdmin;

/// <summary>
/// One of the four Overview graphs: a title with the current value in it, and the last few minutes of
/// samples as a line, exactly the strip SSMS puts above its grids.
/// </summary>
/// <remarks>
/// Drawn rather than charted. The host ships no charting library and a plugin may not add one for four
/// sparklines; <c>ErDiagramCanvas</c> set the precedent that a plugin draws its own picture. Colours are
/// mid-tone and semi-transparent so they hold up on the light and the dark theme without the plugin
/// reaching for host theme resources, which it cannot see across the load-context boundary.
/// </remarks>
internal sealed class ActivityChart : Control
{
    /// <summary>Samples kept per graph. At the default ten-second refresh this is the last ten minutes —
    /// long enough to see a spike arrive and pass, short enough that the line is not a smear.</summary>
    public const int Capacity = 60;

    private static readonly IBrush Hairline = new SolidColorBrush(Color.FromArgb(45, 128, 128, 128));
    private static readonly IBrush Gridline = new SolidColorBrush(Color.FromArgb(25, 128, 128, 128));

    private readonly List<double?> _points = [];
    private readonly string _title;
    private readonly double? _fixedMax;
    private readonly IBrush _line;
    private readonly IBrush _fill;

    private string _caption;

    public ActivityChart(string title, Color colour, double? fixedMax = null)
    {
        _title = title;
        _caption = title;
        _fixedMax = fixedMax;
        _line = new SolidColorBrush(colour);
        _fill = new SolidColorBrush(colour, 0.16);
        Height = 110;
        MinWidth = 160;
    }

    /// <summary>Add the newest sample. A null value means "the server could not tell us" (the CPU ring
    /// buffer on Azure SQL Database) and leaves a gap rather than drawing a zero, which would read as an
    /// idle server.</summary>
    public void Add(double? value, string display)
    {
        _points.Add(value);
        if (_points.Count > Capacity)
        {
            _points.RemoveRange(0, _points.Count - Capacity);
        }

        _caption = $"{_title} ({display})";
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var text = TextElement.GetForeground(this) ?? Hairline;
        var caption = new FormattedText(
            _caption,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            12,
            text);

        context.DrawText(caption, new Point(2, 0));

        var plot = new Rect(0, caption.Height + 4, Bounds.Width, Math.Max(Bounds.Height - caption.Height - 6, 1));
        context.DrawRectangle(null, new Pen(Hairline), plot);

        for (var i = 1; i < 4; i++)
        {
            var y = plot.Y + plot.Height * i / 4;
            context.DrawLine(new Pen(Gridline), new Point(plot.X, y), new Point(plot.Right, y));
        }

        // Scale to a fixed ceiling where the unit has one (CPU is a percentage), otherwise to the tallest
        // sample with headroom, so a quiet graph does not amplify noise into a mountain range.
        var values = _points.Where(p => p.HasValue).Select(p => p!.Value).ToList();
        if (values.Count < 2)
        {
            return;
        }

        var max = _fixedMax ?? Math.Max(values.Max() * 1.25, 1);
        var step = plot.Width / (Capacity - 1);

        // Newest sample at the right edge, so the line scrolls leftwards as SSMS's does.
        var points = _points
            .Select((value, i) => (Value: value, Index: i))
            .Where(p => p.Value.HasValue)
            .Select(p => new Point(
                plot.Right - (_points.Count - 1 - p.Index) * step,
                plot.Bottom - plot.Height * Math.Clamp(p.Value!.Value / max, 0, 1)))
            .ToList();

        var area = new StreamGeometry();
        using (var draw = area.Open())
        {
            draw.BeginFigure(new Point(points[0].X, plot.Bottom), isFilled: true);
            foreach (var point in points)
            {
                draw.LineTo(point);
            }

            draw.LineTo(new Point(points[^1].X, plot.Bottom));
            draw.EndFigure(isClosed: true);
        }

        // Fill and line are drawn separately: filling a closed figure with a pen would also stroke the two
        // vertical drops to the baseline, which read as spikes that are not in the data.
        context.DrawGeometry(_fill, null, area);

        var pen = new Pen(_line, 1.5);
        for (var i = 1; i < points.Count; i++)
        {
            context.DrawLine(pen, points[i - 1], points[i]);
        }
    }
}
