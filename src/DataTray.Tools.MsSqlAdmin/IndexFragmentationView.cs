using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Threading;

namespace DataTray.Tools.MsSqlAdmin;

/// <summary>
/// The body of the confirmation dialog for all seven index actions (SE-249/SE-253): the table's indexes
/// with their current fragmentation and page count, read from <c>sys.dm_db_index_physical_stats</c> the
/// moment the dialog opens.
/// </summary>
/// <remarks>
/// The host's generic dialog renders a tool's declared <c>ToolField</c>s or its own view, and these tools
/// have neither — the action is the whole input — so the dialog came up as a title and two buttons with
/// nothing in between, asking "rebuild every index on this table?" while showing nothing about which of
/// them are fragmented. That is what SSMS puts here, and it is the number the decision turns on: a 2%
/// index does not need the outage a rebuild costs.
/// <para>One view for all seven, built by <see cref="IndexToolBase"/>, because the question is the same in
/// every case — only the scope differs, and that is the <c>index</c> argument.</para>
/// </remarks>
internal sealed class IndexFragmentationView : UserControl
{
    /// <summary>Pre-formatted for display: the grid is read, not computed on, and formatting in the row
    /// keeps the culture decision in one place rather than in four column templates.</summary>
    private sealed record IndexRow(string Name, string Type, string Fragmentation, string Pages);

    private readonly IToolUiContext _context;
    private readonly DataGrid _grid;
    private readonly TextBlock _status;

    /// <param name="index">The single index to report on, or null for the folder actions, which act on
    /// every index of the table and so show every index of the table.</param>
    public IndexFragmentationView(IToolUiContext context, string? description, string? index)
    {
        _context = context;
        var loc = context.Localizer;

        _grid = new DataGrid
        {
            IsReadOnly = true,
            CanUserReorderColumns = false,
            CanUserResizeColumns = true,
            CanUserSortColumns = false,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            FontSize = 12,
            IsVisible = false
        };
        _grid.Columns.Add(Column(loc["index.fragmentation.column.index"], nameof(IndexRow.Name),
            new DataGridLength(1, DataGridLengthUnitType.Star)));
        _grid.Columns.Add(Column(loc["index.fragmentation.column.type"], nameof(IndexRow.Type),
            new DataGridLength(1, DataGridLengthUnitType.Auto)));
        _grid.Columns.Add(Column(loc["index.fragmentation.column.fragmentation"], nameof(IndexRow.Fragmentation),
            new DataGridLength(1, DataGridLengthUnitType.Auto)));
        _grid.Columns.Add(Column(loc["index.fragmentation.column.pages"], nameof(IndexRow.Pages),
            new DataGridLength(1, DataGridLengthUnitType.Auto)));

        _status = new TextBlock
        {
            Text = loc["index.fragmentation.loading"],
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            FontSize = 11.5,
            Margin = new Thickness(0, 6, 0, 0)
        };

        var header = new StackPanel { Spacing = 6 };
        if (!string.IsNullOrWhiteSpace(description))
        {
            header.Children.Add(new TextBlock
            {
                Text = description,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11.5,
                Opacity = 0.8
            });
        }

        header.Children.Add(new TextBlock
        {
            Text = loc["index.fragmentation.heading"],
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 4, 0, 0)
        });

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetRow(header, 0);
        Grid.SetRow(_grid, 1);
        Grid.SetRow(_status, 2);
        grid.Children.Add(header);
        grid.Children.Add(_grid);
        grid.Children.Add(_status);
        Content = grid;

        _ = LoadAsync(index);
    }

    private static DataGridTextColumn Column(string header, string property, DataGridLength width) => new()
    {
        Header = header,
        Binding = new Binding(property),
        Width = width,
        CanUserSort = false
    };

    private async Task LoadAsync(string? index)
    {
        var table = _context.Ancestor(DbNodeKind.Table);
        if (string.IsNullOrEmpty(table))
        {
            // The same gap ExecuteAsync refuses on: without the ancestry there is no way to know which
            // table's indexes these are, and guessing from the index name is wrong the moment two tables
            // share one.
            Show(null, _context.Localizer["index.error.noTable"]);
            return;
        }

        var schema = _context.Ancestor(DbNodeKind.Schema);
        var sql = IndexStatements.FragmentationStats(_context.Provider.Dialect, schema, table, index);

        try
        {
            var result = await _context.QueryAsync(sql, CancellationToken.None);
            // "as" rather than Convert: a NULL arrives as DBNull, which Convert.ToDecimal would throw on —
            // the same idiom ShrinkFileView reads its file sizes with.
            var rows = result.Rows.Select(r => new IndexRow(
                r[0] as string ?? string.Empty,
                DisplayType(r[1] as string ?? string.Empty),
                $"{r[2] as decimal? ?? 0m:N2}%",
                $"{r[3] as long? ?? 0L:N0}")).ToList();

            Dispatcher.UIThread.Post(() => Show(
                rows,
                rows.Count == 0 ? _context.Localizer["index.fragmentation.empty"] : null));
        }
        catch (Exception ex)
        {
            // A dialog that cannot read the DMV still has an action to offer — VIEW DATABASE STATE is a
            // permission a user can lack while holding ALTER on the table — so it says so and stays usable
            // rather than refusing the run.
            Dispatcher.UIThread.Post(() => Show(
                null, _context.Localizer.Get("index.fragmentation.unavailable", ex.Message)));
        }
    }

    private void Show(IReadOnlyList<IndexRow>? rows, string? status)
    {
        _grid.ItemsSource = rows;
        _grid.IsVisible = rows is { Count: > 0 };
        _status.Text = status;
        _status.IsVisible = !string.IsNullOrEmpty(status);
    }

    // The catalog's own wording, minus the underscores it uses for spaces.
    private static string DisplayType(string typeDesc) => typeDesc switch
    {
        "CLUSTERED" => "Clustered",
        "NONCLUSTERED" => "Nonclustered",
        "XML" => "XML",
        _ => typeDesc.Replace('_', ' ')
    };
}
