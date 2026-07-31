using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DataTray.Tools.ErDiagram;

/// <summary>How much of each table is drawn. The mockup calls these Names / Keys / Columns.</summary>
public enum ErDetail
{
    Names,
    Keys,
    Columns
}

/// <summary>
/// Draws the diagram. A custom-rendered control rather than a panel of child controls: a diagram is one
/// picture, its boxes have no interactive parts of their own yet, and one <see cref="Render"/> pass is
/// both simpler and what lets the whole thing scale by a single transform later.
///
/// <para>Positions come from <see cref="IErLayout"/> as rank/order; this is where they become pixels,
/// because box height depends on how many rows are drawn and that is a rendering decision.</para>
/// </summary>
public sealed class ErDiagramCanvas : Control
{
    // Geometry. Column width is fixed so ranks line up; height follows the row count.
    private const double BoxWidth = 190;
    private const double HeaderHeight = 26;
    private const double RowHeight = 18;
    private const double GapX = 74;
    private const double GapY = 26;
    private const double Margin = 28;
    private const double Radius = 6;

    private readonly ErGraph _graph;
    private readonly ErLayoutResult _layout;
    private readonly ErDetail _detail;
    private readonly ErPalette _palette;

    private readonly Dictionary<string, Rect> _boxes = new(StringComparer.OrdinalIgnoreCase);

    public ErDiagramCanvas(ErGraph graph, ErLayoutResult layout, ErDetail detail, ErPalette palette)
    {
        _graph = graph;
        _layout = layout;
        _detail = detail;
        _palette = palette;

        MeasureBoxes();
    }

    /// <summary>The drawing's own size, so a scroll viewer can scroll it.</summary>
    public Size DiagramSize { get; private set; }

    private void MeasureBoxes()
    {
        // Lay each rank out as a column, top-aligned. Vertical centring per rank would look tidier and is
        // deliberately not done yet: it moves boxes relative to each other, which is a layout decision,
        // and layout lives behind IErLayout rather than in the renderer.
        var byKey = _graph.Nodes.ToDictionary(n => n.Key, n => n, StringComparer.OrdinalIgnoreCase);
        double maxX = 0, maxY = 0;

        var offsets = new Dictionary<int, double>();

        foreach (var placement in _layout.Placements.OrderBy(p => p.Rank).ThenBy(p => p.Order))
        {
            var rows = RowsFor(byKey[placement.Key]);
            var height = HeaderHeight + rows * RowHeight + 6;

            var x = Margin + placement.Rank * (BoxWidth + GapX);
            var y = Margin + offsets.GetValueOrDefault(placement.Rank);
            offsets[placement.Rank] = offsets.GetValueOrDefault(placement.Rank) + height + GapY;

            _boxes[placement.Key] = new Rect(x, y, BoxWidth, height);
            maxX = Math.Max(maxX, x + BoxWidth);
            maxY = Math.Max(maxY, y + height);
        }

        DiagramSize = new Size(maxX + Margin, maxY + Margin);
        Width = DiagramSize.Width;
        Height = DiagramSize.Height;
    }

    private int RowsFor(ErNode node) => _detail switch
    {
        ErDetail.Names => 0,
        ErDetail.Columns => node.Table.Columns.Count,
        _ => KeyColumns(node).Count
    };

    /// <summary>Primary-key and foreign-key columns, in table order — the "Keys" level, which the mockup
    /// calls the middle ground because most diagrams are read for their relations.</summary>
    private static List<ColumnDef> KeyColumns(ErNode node)
    {
        var keys = new HashSet<string>(node.Table.PrimaryKey?.Columns ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var fk in node.Table.ForeignKeys)
        {
            keys.UnionWith(fk.Columns);
        }

        return node.Table.Columns.Where(c => keys.Contains(c.Name)).OrderBy(c => c.Ordinal).ToList();
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(_palette.Canvas, new Rect(Bounds.Size));

        // Relations first so the lines pass behind the boxes rather than over their text.
        foreach (var edge in _graph.Edges.Where(e => !e.IsSelfReference))
        {
            DrawRelation(context, edge);
        }

        var byKey = _graph.Nodes.ToDictionary(n => n.Key, n => n, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, box) in _boxes)
        {
            DrawTable(context, byKey[key], box);
        }
    }

    private void DrawRelation(DrawingContext context, ErEdge edge)
    {
        if (!_boxes.TryGetValue(edge.FromKey, out var from) || !_boxes.TryGetValue(edge.ToKey, out var to))
        {
            return;
        }

        // The referenced table sits to the left, so the line leaves the child's left edge and arrives at
        // the parent's right edge. An elbow rather than a diagonal: with boxes on a grid, right angles
        // read as structure and diagonals read as noise.
        //
        // The child end attaches at the row of the column that actually holds the key, so two foreign keys
        // into the same table are two distinct lines rather than one drawn twice.
        var start = new Point(from.X, RowY(edge.FromKey, from, edge.ForeignKey.Columns.FirstOrDefault()));
        var end = new Point(to.Right, to.Y + HeaderHeight / 2);
        var midX = (start.X + end.X) / 2;

        var pen = new Pen(_palette.Relation, 1.2);
        context.DrawLine(pen, start, new Point(midX, start.Y));
        context.DrawLine(pen, new Point(midX, start.Y), new Point(midX, end.Y));
        context.DrawLine(pen, new Point(midX, end.Y), end);

        DrawCrowsFoot(context, pen, start);
    }

    /// <summary>
    /// The many side, at the referencing table: three prongs converging on a point out along the line and
    /// opening onto the box edge. Drawing it the other way round — converging on the box — makes an
    /// arrowhead, which is the notation for the one side and says the opposite of what is meant.
    /// </summary>
    private static void DrawCrowsFoot(DrawingContext context, Pen pen, Point at)
    {
        const double length = 9;
        const double spread = 4.5;
        var apex = new Point(at.X - length, at.Y);
        context.DrawLine(pen, apex, new Point(at.X, at.Y - spread));
        context.DrawLine(pen, apex, at);
        context.DrawLine(pen, apex, new Point(at.X, at.Y + spread));
    }

    /// <summary>
    /// The y of one column's row inside a box, so a relation line meets the key it belongs to. Falls back
    /// to the header's midpoint when that column is not drawn — at the Names detail level nothing is.
    /// </summary>
    private double RowY(string tableKey, Rect box, string? column)
    {
        var header = box.Y + HeaderHeight / 2;
        if (column is null || _detail == ErDetail.Names)
        {
            return header;
        }

        var node = _graph.Nodes.FirstOrDefault(n => string.Equals(n.Key, tableKey, StringComparison.OrdinalIgnoreCase));
        if (node is null)
        {
            return header;
        }

        var rows = _detail == ErDetail.Columns
            ? node.Table.Columns.OrderBy(c => c.Ordinal).ToList()
            : KeyColumns(node);

        var index = rows.FindIndex(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? header : box.Y + HeaderHeight + 3 + index * RowHeight + RowHeight / 2 - 3;
    }

    private void DrawTable(DrawingContext context, ErNode node, Rect box)
    {
        var rounded = new RoundedRect(box, Radius);
        context.DrawRectangle(_palette.Box, new Pen(_palette.Hairline, 1), rounded);

        // Header strip, square at the bottom so it reads as part of the box.
        var header = new Rect(box.X, box.Y, box.Width, HeaderHeight);
        context.DrawRectangle(_palette.Header, null, new RoundedRect(header, Radius, Radius, 0, 0));
        context.DrawLine(new Pen(_palette.Hairline, 1),
            new Point(box.X, box.Y + HeaderHeight), new Point(box.Right, box.Y + HeaderHeight));

        Draw(context, node.Table.Name, box.X + 10, box.Y + 5, 12.5, _palette.Text, FontWeight.SemiBold);

        if (_detail == ErDetail.Names)
        {
            return;
        }

        var columns = _detail == ErDetail.Columns
            ? node.Table.Columns.OrderBy(c => c.Ordinal).ToList()
            : KeyColumns(node);

        var primaryKey = new HashSet<string>(node.Table.PrimaryKey?.Columns ?? [], StringComparer.OrdinalIgnoreCase);
        var foreignKeys = new HashSet<string>(
            node.Table.ForeignKeys.SelectMany(f => f.Columns), StringComparer.OrdinalIgnoreCase);

        var y = box.Y + HeaderHeight + 3;
        foreach (var column in columns)
        {
            var badge = primaryKey.Contains(column.Name) ? "PK" : foreignKeys.Contains(column.Name) ? "FK" : "";
            if (badge.Length > 0)
            {
                Draw(context, badge, box.X + 10, y, 9.5,
                    badge == "PK" ? _palette.KeyPrimary : _palette.KeyForeign, FontWeight.Bold);
            }

            Draw(context, column.Name, box.X + 34, y, 11, _palette.Text, FontWeight.Normal);
            Draw(context, column.DataType, box.Right - 10, y, 10, _palette.TextFaint, FontWeight.Normal,
                alignRight: true);

            y += RowHeight;
        }
    }

    private static void Draw(
        DrawingContext context, string text, double x, double y, double size, IBrush brush,
        FontWeight weight, bool alignRight = false)
    {
        var formatted = new FormattedText(
            text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, weight), size, brush);

        context.DrawText(formatted, new Point(alignRight ? x - formatted.Width : x, y));
    }
}

/// <summary>
/// The diagram's colours. Passed in rather than read from resources: a plugin cannot reach the host's
/// theme dictionary across the ALC boundary, so the host-facing side resolves light or dark and hands the
/// result over.
/// </summary>
public sealed record ErPalette(
    IBrush Canvas,
    IBrush Box,
    IBrush Header,
    IBrush Hairline,
    IBrush Text,
    IBrush TextFaint,
    IBrush Relation,
    IBrush KeyPrimary,
    IBrush KeyForeign)
{
    public static ErPalette Light => new(
        Canvas: new SolidColorBrush(Color.Parse("#FBFCFD")),
        Box: new SolidColorBrush(Color.Parse("#FFFFFF")),
        Header: new SolidColorBrush(Color.Parse("#F0F1F3")),
        Hairline: new SolidColorBrush(Color.Parse("#D9DCE1")),
        Text: new SolidColorBrush(Color.Parse("#1B1D21")),
        TextFaint: new SolidColorBrush(Color.Parse("#8A909A")),
        Relation: new SolidColorBrush(Color.Parse("#9AA3AF")),
        KeyPrimary: new SolidColorBrush(Color.Parse("#B9791F")),
        KeyForeign: new SolidColorBrush(Color.Parse("#2F6FEB")));

    public static ErPalette Dark => new(
        Canvas: new SolidColorBrush(Color.Parse("#1E1F22")),
        Box: new SolidColorBrush(Color.Parse("#2B2D30")),
        Header: new SolidColorBrush(Color.Parse("#393B40")),
        Hairline: new SolidColorBrush(Color.Parse("#4A4D52")),
        Text: new SolidColorBrush(Color.Parse("#DFE1E5")),
        TextFaint: new SolidColorBrush(Color.Parse("#8A909A")),
        Relation: new SolidColorBrush(Color.Parse("#6B7280")),
        KeyPrimary: new SolidColorBrush(Color.Parse("#D9A441")),
        KeyForeign: new SolidColorBrush(Color.Parse("#3574F0")));
}
