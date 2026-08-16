using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;

namespace DataTray.Tools.MsSqlAdmin;

/// <summary>One cell of a monitor grid: a string that can say when it changed.</summary>
/// <remarks>
/// A notifying object per cell rather than a plain string in the row, because a column binds to a property
/// path and re-reads it only when that property announces itself. A row's own indexer announces nothing a
/// binding listens to, so cells bound to <c>[0]</c>, <c>[1]</c>, … kept showing the values they were
/// realised with — every grid in the tab was frozen on its first sample, and a header click reordered the
/// rows behind a screen that never redrew (SE-265). <c>Cells[i].Value</c> is the shape the host's own
/// result grid binds to, for exactly this reason.
/// </remarks>
internal sealed class GridCell(string value) : INotifyPropertyChanged
{
    private string _value = value;

    public string Value
    {
        get => _value;
        set
        {
            if (string.Equals(_value, value, StringComparison.Ordinal))
            {
                return;
            }

            _value = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// One row of a monitor grid. One row type serves all five grids, and the cells are written in place on
/// refresh: keeping the row objects alive is what lets the scroll position and the selected row survive a
/// refresh, which a grid you are reading while it reloads every ten seconds absolutely needs.
/// </summary>
internal sealed class GridRow(string[] cells)
{
    /// <summary>An array, not any read-only list: a binding to <c>Cells[i]</c> reaches the element through
    /// <see cref="System.Collections.IList"/>, which an array implements and a read-only wrapper does not —
    /// bind to one of those and every cell in the grid renders empty.</summary>
    public GridCell[] Cells { get; } = [.. cells.Select(c => new GridCell(c))];

    /// <summary>The cells as plain text — what a row action reads to know what it acts on.</summary>
    public string[] Values => [.. Cells.Select(c => c.Value)];

    public void Replace(string[] cells)
    {
        for (var i = 0; i < Cells.Length; i++)
        {
            Cells[i].Value = i < cells.Length ? cells[i] : string.Empty;
        }
    }
}

/// <summary>
/// A collapsible section of the Activity Monitor: SSMS's header bar that folds the whole grid away, a
/// Database dropdown, a filter over one column or all of them, and the grid itself.
/// </summary>
/// <remarks>
/// Sorting is done here rather than by the DataGrid, the same way the host's own result grid does it: the
/// built-in sort is cancelled and the rows are ordered by this class, because the rows are replaced in
/// place on every refresh and a sort the grid owns would silently be applied to values that have since
/// changed. Numeric-looking columns sort numerically (<see cref="ActivityRates.CompareCells"/>), so "9"
/// does not come after "10", and the sort is stable, so rows a column cannot tell apart stay put instead
/// of reshuffling every ten seconds.
/// </remarks>
internal sealed class ActivityGrid
{
    private readonly IPluginLocalizer _loc;
    private readonly string[] _headers;
    private readonly DataGrid _grid;
    private readonly ObservableCollection<GridRow> _rows = [];
    private readonly ComboBox _database = new() { MinWidth = 140, FontSize = 12 };
    private readonly ComboBox _filterColumn = new() { MinWidth = 150, FontSize = 12 };
    private readonly TextBox _filterText = new() { Width = 200, FontSize = 12 };
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, FontSize = 12 };

    private readonly int _databaseColumn;
    private readonly int _fullTextColumn;

    private IReadOnlyList<string[]> _all = [];
    private int _sortColumn = -1;
    private bool _sortDescending;

    /// <param name="filterColumn">Column the filter box starts on, by header index, and
    /// <paramref name="filterText"/> what it starts filtering for. The Processes grid opens filtered to
    /// user processes, as SSMS does: sixty background tasks above the four sessions you came to look at is
    /// not a monitor, and the filter is in plain sight to be cleared.</param>
    /// <param name="databaseColumn">Header index of the grid's Database column, or -1 for a grid that has
    /// none. It gets a dropdown of its own beside the free-text filter, because picking one of the databases
    /// that actually have rows is the question this tab is opened with, and it is a different question from
    /// the one the text box answers — the two stack.</param>
    /// <param name="fullTextColumn">Row index holding the untruncated text a double-click shows in a window
    /// (<see cref="ActivityTables.FullTextColumn"/>), or -1 for a grid whose cells are all short enough to
    /// read where they are.</param>
    public ActivityGrid(
        IPluginLocalizer loc,
        string title,
        string[] headers,
        double[] widths,
        double height = 220,
        int filterColumn = -1,
        string filterText = "",
        int databaseColumn = -1,
        int fullTextColumn = -1)
    {
        _loc = loc;
        _headers = headers;
        _databaseColumn = databaseColumn;
        _fullTextColumn = fullTextColumn;

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
                Binding = new Binding($"Cells[{i}].Value"),
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
        _grid.DoubleTapped += async (_, _) => await ShowFullTextAsync();

        _filterColumn.ItemsSource = new[] { loc.Get("activity.filterAll") }.Concat(headers).ToList();
        _filterColumn.SelectedIndex = filterColumn >= 0 ? filterColumn + 1 : 0;
        _filterText.Text = filterText;
        _filterColumn.SelectionChanged += (_, _) => Render();
        _filterText.TextChanged += (_, _) => Render();

        _database.IsVisible = databaseColumn >= 0;
        _database.SelectionChanged += (_, _) => Render();
        RefreshDatabases();

        var label = new TextBlock
        {
            Text = _loc.Get("activity.database"),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            IsVisible = databaseColumn >= 0
        };

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 0, 0, 6),
            Children = { label, _database, _filterColumn, _filterText, _count }
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
    public string[]? SelectedCells => (_grid.SelectedItem as GridRow)?.Values;

    /// <summary>Attach a right-click action to the rows (the Processes grid's Kill Process).</summary>
    public void SetRowMenu(ContextMenu menu) => _grid.ContextMenu = menu;

    public void Update(IReadOnlyList<string[]> rows)
    {
        _all = rows;
        RefreshDatabases();
        Render();
    }

    // The dropdown lists the databases in the whole snapshot, not in what the text filter left — the two
    // filters are independent, so narrowing one must not take the other's choices away. Rewritten only when
    // the set actually changes, or a refresh every ten seconds would drop the list open under the pointer.
    private void RefreshDatabases()
    {
        if (_databaseColumn < 0)
        {
            return;
        }

        var names = ActivityTables.Databases(_all, _databaseColumn, _loc.Get("activity.allDatabases"));
        if (_database.ItemsSource is IReadOnlyList<string> current && names.SequenceEqual(current))
        {
            return;
        }

        // A database that no longer has rows falls back to "(all databases)" rather than filtering
        // everything away — the selection is only worth keeping while it can still match something.
        var selected = _database.SelectedItem as string;
        _database.ItemsSource = names;
        _database.SelectedIndex = selected is not null ? Math.Max(names.ToList().IndexOf(selected), 0) : 0;
    }

    // Double-click a row to read the whole thing: the query column is one line of collapsed whitespace
    // clipped to its column width, which is enough to recognise a statement and not enough to read one.
    private async Task ShowFullTextAsync()
    {
        if (_fullTextColumn < 0 ||
            SelectedCells is not { } cells ||
            _fullTextColumn >= cells.Length ||
            cells[_fullTextColumn].Length == 0 ||
            TopLevel.GetTopLevel(_grid) is not Window owner)
        {
            return;
        }

        await QueryTextWindow.ShowAsync(owner, _loc, cells[_fullTextColumn]);
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
        // Index 0 is "(all databases)"; anything below it is an exact database, not a substring, because the
        // dropdown offers names that exist and "db" matching "db_archive" would be a different feature.
        if (_databaseColumn >= 0 && _database.SelectedIndex > 0 && _database.SelectedItem is string database)
        {
            rows = rows.Where(r => string.Equals(
                Cell(r, _databaseColumn), database, StringComparison.CurrentCultureIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(filter))
        {
            rows = column >= 0
                ? rows.Where(r => Cell(r, column).Contains(filter, StringComparison.CurrentCultureIgnoreCase))
                : rows.Where(r => r.Any(c => c.Contains(filter, StringComparison.CurrentCultureIgnoreCase)));
        }

        var list = rows.ToList();
        if (_sortColumn >= 0)
        {
            // OrderBy, not List.Sort: it is stable, so the rows a sorted column cannot tell apart — and
            // "0" is most of what a quiet server reports — keep the server's own order instead of
            // reshuffling among themselves on every refresh.
            var sign = _sortDescending ? -1 : 1;
            list = [.. list.OrderBy(
                r => Cell(r, _sortColumn),
                Comparer<string>.Create((a, b) => ActivityRates.CompareCells(a, b) * sign))];
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
}
