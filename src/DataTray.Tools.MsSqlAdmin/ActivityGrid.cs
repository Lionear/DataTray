using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;

namespace DataTray.Tools.MsSqlAdmin;

/// <summary>
/// One row of a monitor grid. The cells are bound by index (<c>[0]</c>, <c>[1]</c>, …) so one row type
/// serves all five grids, and they are replaced in place on refresh: keeping the row objects alive is what
/// lets the scroll position and the selected row survive a refresh, which a grid you are reading while it
/// reloads every ten seconds absolutely needs.
/// </summary>
internal sealed class GridRow(string[] cells) : INotifyPropertyChanged
{
    public string[] Cells { get; private set; } = cells;

    public string this[int index] => index >= 0 && index < Cells.Length ? Cells[index] : string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Replace(string[] cells)
    {
        Cells = cells;
        // "Item[]" is the framework's name for "every indexer value changed" — one notification rather than
        // one per column.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }
}

/// <summary>
/// A collapsible section of the Activity Monitor: SSMS's header bar that folds the whole grid away, a
/// filter over one column or all of them, and the grid itself.
/// </summary>
/// <remarks>
/// Sorting is done here rather than by the DataGrid, the same way the host's own result grid does it: the
/// built-in sort is cancelled and the rows are ordered by this class, because the rows are replaced in
/// place on every refresh and a sort the grid owns would silently be applied to values that have since
/// changed. Numeric-looking columns sort numerically, so "9" does not come after "10".
/// </remarks>
internal sealed class ActivityGrid
{
    private readonly string[] _headers;
    private readonly DataGrid _grid;
    private readonly ObservableCollection<GridRow> _rows = [];
    private readonly ComboBox _filterColumn = new() { MinWidth = 150, FontSize = 12 };
    private readonly TextBox _filterText = new() { Width = 200, FontSize = 12 };
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, FontSize = 12 };

    private IReadOnlyList<string[]> _all = [];
    private int _sortColumn = -1;
    private bool _sortDescending;

    /// <param name="filterColumn">Column the filter box starts on, by header index, and
    /// <paramref name="filterText"/> what it starts filtering for. The Processes grid opens filtered to
    /// user processes, as SSMS does: sixty background tasks above the four sessions you came to look at is
    /// not a monitor, and the filter is in plain sight to be cleared.</param>
    public ActivityGrid(
        string title,
        string[] headers,
        double[] widths,
        string filterAllLabel,
        double height = 220,
        int filterColumn = -1,
        string filterText = "")
    {
        _headers = headers;

        _grid = new DataGrid
        {
            IsReadOnly = true,
            AutoGenerateColumns = false,
            CanUserResizeColumns = true,
            CanUserReorderColumns = true,
            SelectionMode = DataGridSelectionMode.Single,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            MaxHeight = height,
            ItemsSource = _rows,
            FontSize = 12
        };

        for (var i = 0; i < headers.Length; i++)
        {
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = headers[i],
                Binding = new Binding($"[{i}]"),
                // A width of 0 means "as wide as it needs to be": these headers are long ("Recent Wait Time
                // (ms/sec)") beside cells that are short, and a pixel width guessed per column clipped the
                // headers into riddles. Only the columns holding a whole query or a file path are pinned,
                // because those would otherwise take the entire window.
                Width = i < widths.Length && widths[i] > 0
                    ? new DataGridLength(widths[i], DataGridLengthUnitType.Pixel)
                    : new DataGridLength(1, DataGridLengthUnitType.Auto),
                CanUserSort = true,
                Tag = i
            });
        }

        _grid.Sorting += OnSorting;

        _filterColumn.ItemsSource = new[] { filterAllLabel }.Concat(headers).ToList();
        _filterColumn.SelectedIndex = filterColumn >= 0 ? filterColumn + 1 : 0;
        _filterText.Text = filterText;
        _filterColumn.SelectionChanged += (_, _) => Render();
        _filterText.TextChanged += (_, _) => Render();

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 0, 0, 6),
            Children = { _filterColumn, _filterText, _count }
        };

        Section = new Expander
        {
            Header = title,
            IsExpanded = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 8),
            Content = new StackPanel { Children = { bar, _grid } }
        };
    }

    /// <summary>The whole section, ready to drop into the tab.</summary>
    public Expander Section { get; }

    /// <summary>The cells of the selected row, or null when nothing is selected — what a row action
    /// (Kill Process) reads to know what it acts on.</summary>
    public string[]? SelectedCells => (_grid.SelectedItem as GridRow)?.Cells;

    /// <summary>Attach a right-click action to the rows (the Processes grid's Kill Process).</summary>
    public void SetRowMenu(ContextMenu menu) => _grid.ContextMenu = menu;

    public void Update(IReadOnlyList<string[]> rows)
    {
        _all = rows;
        Render();
    }

    /// <summary>Show a failure in place of the rows, so one broken section says why instead of the tab
    /// looking merely empty.</summary>
    public void ShowError(string message)
    {
        _all = [];
        _rows.Clear();
        _count.Text = message;
    }

    private void OnSorting(object? sender, DataGridColumnEventArgs e)
    {
        // Cancel the DataGrid's own sort: it would reorder row objects this class replaces in place.
        e.Handled = true;
        if (e.Column.Tag is not int column)
        {
            return;
        }

        _sortDescending = _sortColumn == column && !_sortDescending;
        _sortColumn = column;

        for (var i = 0; i < _grid.Columns.Count; i++)
        {
            var arrow = i == column ? (_sortDescending ? " ▼" : " ▲") : string.Empty;
            _grid.Columns[i].Header = _headers[i] + arrow;
        }

        Render();
    }

    // Filter, then sort, then push into the live collection by position — replacing each row's cells rather
    // than the row itself, so the grid keeps its scroll offset and selection across a refresh.
    private void Render()
    {
        var filter = _filterText.Text;
        var column = _filterColumn.SelectedIndex - 1;

        IEnumerable<string[]> rows = _all;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            rows = column >= 0
                ? rows.Where(r => Cell(r, column).Contains(filter, StringComparison.CurrentCultureIgnoreCase))
                : rows.Where(r => r.Any(c => c.Contains(filter, StringComparison.CurrentCultureIgnoreCase)));
        }

        var list = rows.ToList();
        if (_sortColumn >= 0)
        {
            list.Sort((a, b) => Compare(Cell(a, _sortColumn), Cell(b, _sortColumn)) * (_sortDescending ? -1 : 1));
        }

        for (var i = 0; i < list.Count; i++)
        {
            if (i < _rows.Count)
            {
                _rows[i].Replace(list[i]);
            }
            else
            {
                _rows.Add(new GridRow(list[i]));
            }
        }

        while (_rows.Count > list.Count)
        {
            _rows.RemoveAt(_rows.Count - 1);
        }

        _count.Text = list.Count == _all.Count
            ? _all.Count.ToString(CultureInfo.CurrentCulture)
            : $"{list.Count} / {_all.Count}";
    }

    private static string Cell(string[] row, int index) => index < row.Length ? row[index] : string.Empty;

    // Numbers as numbers ("9" before "10"), everything else as text. The cells are already formatted for
    // display, so the parse has to accept the thousands separators that formatting put there.
    private static int Compare(string a, string b)
    {
        if (double.TryParse(a, NumberStyles.Any, CultureInfo.CurrentCulture, out var x)
            && double.TryParse(b, NumberStyles.Any, CultureInfo.CurrentCulture, out var y))
        {
            return x.CompareTo(y);
        }

        return string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);
    }
}
