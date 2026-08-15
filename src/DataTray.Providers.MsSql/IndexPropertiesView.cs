using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DataTray.Sdk.Ui;
using Microsoft.Data.SqlClient;

namespace DataTray.Providers.MsSql;

/// <summary>
/// SSMS' "Index Properties" for SQL Server (SE-252), serving both entry points: "New Index…" on a table's
/// Indexes folder and "Properties…" on an index. One view, because it is one dialog in SSMS and the only
/// difference is whether there is an existing index to load.
/// </summary>
/// <remarks>
/// Same shape as <see cref="AgentJobPropertiesView"/> — page rail on the left, lazily built pages on the
/// right, one status line at the bottom that every page feeds — and the same write path, running its DDL
/// through the provider the context hands over. The host leaves off its Close row for this view
/// (<c>ICustomNodeInfoUi.InfoViewOwnsActionBar</c>), so OK/Cancel here are the only buttons.
///
/// <para><b>Commit on OK, not save-per-page.</b> Agent job properties saves the page you are on; that cannot
/// work here, because one <c>CREATE INDEX … DROP_EXISTING</c> carries General, Options, Storage and Filter
/// together. Saving a page at a time would issue two rebuilds and let the second undo the first.</para>
///
/// <para>Phase 1 is the General page. The options the later pages will edit are already read and re-emitted
/// on every rebuild (see <see cref="IndexDefinition"/>), because DROP_EXISTING resets whatever the statement
/// does not restate — leaving them out would mean changing a key column quietly reverted settings the user
/// never opened.</para>
/// </remarks>
public sealed class IndexPropertiesView : UserControl
{
    // Phase 1 ships General alone; Options/Storage/Filter (phase 2) and Fragmentation/Extended Properties
    // (phase 3) join it here. The rail hides itself while there is only one page — a rail with a single
    // entry is chrome that cannot do anything.
    private static readonly string[] Pages = ["General"];

    /// <summary>A table column as the picker and the grids show it.</summary>
    private sealed record TableColumn(string Name, string Type, bool Nullable, bool Identity);

    private readonly NodeInfoContext _context;
    private readonly string _table;
    private readonly string? _schema;
    private readonly bool _creating;

    private readonly ContentControl _host = new();
    private readonly Control?[] _built = new Control?[Pages.Length];

    private readonly TextBlock _status = new()
    {
        Opacity = 0.75,
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center
    };

    private readonly Button _ok = new() { Content = "OK", MinWidth = 96, Classes = { "Accent" }, IsEnabled = false };
    private readonly Button _script = new() { Content = "Script", MinWidth = 80, IsEnabled = false };

    // General page editors, kept as fields because OK reads them and the load fills them.
    private readonly TextBox _name = new() { PlaceholderText = "Index name" };
    private readonly ComboBox _type = new() { ItemsSource = new[] { "Nonclustered", "Clustered" }, SelectedIndex = 0 };
    private readonly CheckBox _unique = new() { Content = "Unique" };
    private readonly SelectTable _keys = new(["Name", "Sort order", "Data type", "Nullable"], [0, 110, 140, 80]);
    private readonly SelectTable _included = new(["Name", "Data type", "Nullable"], [0, 140, 80]);
    private readonly ComboBox _addColumn = new() { Width = 220, PlaceholderText = "Add a column…" };

    private readonly List<TableColumn> _tableColumns = [];
    private readonly List<IndexColumn> _columns = [];

    /// <summary>The index as it stands on the server, or null when creating. What OK diffs against.</summary>
    private IndexDefinition? _original;

    private bool _optimizeForSequentialKeySupported;
    private string? _dataSpace;
    private string? _partitionColumn;
    private string? _filter;

    /// <param name="creating">True for "New Index…", where <c>context.Node</c> is the Indexes folder and
    /// there is nothing to load; false for "Properties…", where it is the index itself.</param>
    public IndexPropertiesView(NodeInfoContext context, bool creating)
    {
        _context = context;
        _creating = creating;
        _table = context.Ancestor(DbNodeKind.Table)
            ?? throw new InvalidOperationException("This dialog needs the table the index belongs to.");
        _schema = context.Ancestor(DbNodeKind.Schema);

        if (!creating)
        {
            _name.Text = context.Node.Name;
        }

        var rail = new ListBox
        {
            Width = 170,
            ItemsSource = Pages,
            SelectedIndex = 0,
            Background = Brushes.Transparent,
            IsVisible = Pages.Length > 1
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(rail, ScrollBarVisibility.Disabled);
        rail.SelectionChanged += (_, _) => ShowPage(rail.SelectedIndex);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(rail, 0);
        Grid.SetColumn(_host, 1);
        body.Children.Add(rail);
        body.Children.Add(_host);

        var cancel = new Button { Content = "Cancel", MinWidth = 96 };
        cancel.Click += (_, _) => Close();
        _ok.Click += async (_, _) => await ApplyAsync();
        _script.Click += (_, _) => ScriptToQueryTab();
        // A host older than this member hands over no way to open a query tab, so the button would be a
        // dead control rather than a missing one.
        _script.IsVisible = context.OpenQueryEditor is not null;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _script, _ok, cancel }
        };

        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 12, 0, 0)
        };
        Grid.SetColumn(_status, 0);
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(_status);
        footer.Children.Add(buttons);

        var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        Grid.SetRow(body, 0);
        Grid.SetRow(footer, 1);
        layout.Children.Add(body);
        layout.Children.Add(footer);
        Content = layout;

        ShowPage(0);
        _ = LoadAsync();
    }

    private void ShowPage(int index)
    {
        if (index < 0)
        {
            return;
        }

        _built[index] ??= BuildGeneral();
        _host.Content = _built[index];
    }

    // ── General ──────────────────────────────────────────────────────────────────────────────────────

    private Control BuildGeneral()
    {
        _name.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                Revalidate();
            }
        };
        // IGNORE_DUP_KEY is only legal on a unique index, so the option set has to be rebuilt when this
        // flips — and the Script preview follows the checkbox rather than the last load.
        _unique.IsCheckedChanged += (_, _) => Revalidate();

        var add = new Button { Content = "Add" };
        add.Click += (_, _) => AddSelectedColumn(included: _includedTabSelected);

        var remove = new Button { Content = "Remove" };
        remove.Click += (_, _) => RemoveSelected();

        var up = new Button { Content = "Move up" };
        up.Click += (_, _) => Move(-1);

        var down = new Button { Content = "Move down" };
        down.Click += (_, _) => Move(1);

        // SSMS puts a Sort Order dropdown in the grid cell. These grids are SelectTables — the widget the
        // rest of this provider's dialogs are built from, which shows rows rather than editing them — so the
        // sort order is a button acting on the selected row instead.
        // ponytail: toggle button over an in-cell editor; swap for a DataGrid with a ComboBox column if
        // per-cell editing is ever wanted here for more than this one field.
        var sort = new Button { Content = "Toggle ASC/DESC" };
        sort.Click += (_, _) => ToggleSort();

        // Key and included columns are tabs rather than two stacked grids, so these buttons stay above the
        // fold instead of sinking below the second grid as they do in SSMS. One toolbar serves both tabs:
        // ordering and sort direction are key-column concepts, so they grey out on the other one.
        var tabs = new TabControl
        {
            Items =
            {
                new TabItem { Header = "Key columns", Content = _keys.Control },
                new TabItem { Header = "Included columns", Content = _included.Control }
            }
        };
        tabs.SelectionChanged += (_, _) =>
        {
            _includedTabSelected = tabs.SelectedIndex == 1;
            up.IsEnabled = down.IsEnabled = sort.IsEnabled = !_includedTabSelected;
        };

        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
            Children = { _addColumn, add, remove, up, down, sort }
        };

        var header = new PropPage();
        header.Row("Table name", "table");
        header.Set("table", string.IsNullOrEmpty(_schema) ? _table : $"{_schema}.{_table}");

        return FormBits.Page(
            header.Stack,
            FormBits.Labelled("Index name", _name),
            FormBits.Row("Index type", _type, _unique),
            tabs,
            tools);
    }

    private bool _includedTabSelected;

    // ── Load ─────────────────────────────────────────────────────────────────────────────────────────

    private async Task LoadAsync()
    {
        try
        {
            await using var connection = new SqlConnection(_context.Profile.ConnectionString);
            await connection.OpenAsync();

            await LoadTableColumnsAsync(connection);
            _optimizeForSequentialKeySupported = await SupportsOptimizeForSequentialKeyAsync(connection);

            if (!_creating)
            {
                await LoadIndexAsync(connection);
            }

            Dispatcher.UIThread.Post(() =>
            {
                RefreshGrids();
                RefreshPicker();
                Revalidate();
            });
        }
        catch (Exception ex)
        {
            Report(ex.Message);
        }
    }

    private async Task LoadTableColumnsAsync(SqlConnection connection)
    {
        const string sql = """
            SELECT c.name, t.name, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity
            FROM sys.columns AS c
            JOIN sys.types AS t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID(@table)
            ORDER BY c.column_id
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@table", Qualified);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            _tableColumns.Add(new TableColumn(
                reader.GetString(0),
                TypeName(reader.GetString(1), reader.GetInt16(2), reader.GetByte(3), reader.GetByte(4)),
                reader.GetBoolean(5),
                reader.GetBoolean(6)));
        }
    }

    // OPTIMIZE_FOR_SEQUENTIAL_KEY arrived in SQL Server 2019 and does not parse before it, so emitting it
    // unconditionally would break every rebuild on 2016/2017. COL_LENGTH answers without a version compare.
    private static async Task<bool> SupportsOptimizeForSequentialKeyAsync(SqlConnection connection)
    {
        await using var command = new SqlCommand(
            "SELECT COL_LENGTH('sys.indexes', 'optimize_for_sequential_key')", connection);
        return await command.ExecuteScalarAsync() is int;
    }

    private async Task LoadIndexAsync(SqlConnection connection)
    {
        var sql = $"""
            SELECT i.type_desc, i.is_unique, i.is_padded, i.fill_factor, i.ignore_dup_key,
                   i.allow_row_locks, i.allow_page_locks, i.filter_definition,
                   ds.name, ds.type, ISNULL(st.no_recompute, 0)
                   {(_optimizeForSequentialKeySupported ? ", i.optimize_for_sequential_key" : "")}
            FROM sys.indexes AS i
            JOIN sys.data_spaces AS ds ON ds.data_space_id = i.data_space_id
            LEFT JOIN sys.stats AS st ON st.object_id = i.object_id AND st.stats_id = i.index_id
            WHERE i.object_id = OBJECT_ID(@table) AND i.name = @name
            """;

        bool? optimize = null;
        var clustered = false;
        var unique = false;
        var padded = false;
        var fillFactor = 0;
        var ignoreDup = false;
        var rowLocks = true;
        var pageLocks = true;
        var noRecompute = false;
        var partitioned = false;

        await using (var command = new SqlCommand(sql, connection))
        {
            command.Parameters.AddWithValue("@table", Qualified);
            command.Parameters.AddWithValue("@name", _context.Node.Name);
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                throw new InvalidOperationException($"Index '{_context.Node.Name}' no longer exists on {Qualified}.");
            }

            clustered = reader.GetString(0) == "CLUSTERED";
            unique = reader.GetBoolean(1);
            padded = reader.GetBoolean(2);
            fillFactor = reader.GetByte(3);
            ignoreDup = reader.GetBoolean(4);
            rowLocks = reader.GetBoolean(5);
            pageLocks = reader.GetBoolean(6);
            _filter = reader.IsDBNull(7) ? null : reader.GetString(7);
            _dataSpace = reader.GetString(8);
            partitioned = reader.GetString(9).Trim() == "PS";
            noRecompute = reader.GetBoolean(10);
            if (_optimizeForSequentialKeySupported && !reader.IsDBNull(11))
            {
                optimize = reader.GetBoolean(11);
            }
        }

        await LoadIndexColumnsAsync(connection, partitioned);

        _original = new IndexDefinition
        {
            Schema = _schema,
            Table = _table,
            Name = _context.Node.Name,
            Clustered = clustered,
            Unique = unique,
            Columns = [.. _columns],
            PadIndex = padded,
            FillFactor = fillFactor,
            IgnoreDupKey = ignoreDup,
            StatisticsNoRecompute = noRecompute,
            AllowRowLocks = rowLocks,
            AllowPageLocks = pageLocks,
            OptimizeForSequentialKey = optimize,
            Filter = _filter,
            DataSpace = _dataSpace,
            PartitionColumn = _partitionColumn
        };

        Dispatcher.UIThread.Post(() =>
        {
            _type.SelectedIndex = clustered ? 1 : 0;
            _unique.IsChecked = unique;
        });
    }

    private async Task LoadIndexColumnsAsync(SqlConnection connection, bool partitioned)
    {
        const string sql = """
            SELECT c.name, ic.is_descending_key, ic.is_included_column, ic.partition_ordinal
            FROM sys.index_columns AS ic
            JOIN sys.indexes AS i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
            JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE ic.object_id = OBJECT_ID(@table) AND i.name = @name
            ORDER BY ic.is_included_column, ic.key_ordinal, ic.index_column_id
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@table", Qualified);
        command.Parameters.AddWithValue("@name", _context.Node.Name);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var name = reader.GetString(0);
            // The partitioning column is carried separately: the ON clause names it, and on a partitioned
            // index it is also listed here with key_ordinal 0, where it is not a key column.
            if (partitioned && reader.GetByte(3) > 0)
            {
                _partitionColumn = name;
            }

            _columns.Add(new IndexColumn(name, reader.GetBoolean(1), reader.GetBoolean(2)));
        }
    }

    // ── Grid state ───────────────────────────────────────────────────────────────────────────────────

    private void RefreshGrids()
    {
        _keys.Fill([.. _columns.Where(c => !c.Included).Select(c => new[]
        {
            new Cell(c.Name),
            new Cell(c.Descending ? "Descending" : "Ascending"),
            new Cell(Meta(c.Name)?.Type ?? "—"),
            new Cell(Meta(c.Name) is { Nullable: true } ? "Yes" : "No")
        })], "No key columns — an index needs at least one.");

        _included.Fill([.. _columns.Where(c => c.Included).Select(c => new[]
        {
            new Cell(c.Name),
            new Cell(Meta(c.Name)?.Type ?? "—"),
            new Cell(Meta(c.Name) is { Nullable: true } ? "Yes" : "No")
        })], "None.");
    }

    // Only columns not already in the index — adding one twice is an error the server reports, and the
    // picker is the place to make it impossible instead.
    private void RefreshPicker()
    {
        var used = _columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _addColumn.ItemsSource = _tableColumns.Where(c => !used.Contains(c.Name)).Select(c => c.Name).ToList();
    }

    private TableColumn? Meta(string name) =>
        _tableColumns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    private void AddSelectedColumn(bool included)
    {
        if (_addColumn.SelectedItem is not string name)
        {
            return;
        }

        _columns.Add(new IndexColumn(name, Descending: false, Included: included));
        AfterEdit();
    }

    private void RemoveSelected()
    {
        if (IndexOfSelected() is not { } index)
        {
            return;
        }

        _columns.RemoveAt(index);
        AfterEdit();
    }

    // Key order is the index — moving a column is the whole reason this grid has buttons. Movement stays
    // inside the key columns, since INCLUDE has no order to change.
    private void Move(int offset)
    {
        if (_includedTabSelected || IndexOfSelected() is not { } index)
        {
            return;
        }

        var keys = KeyPositions();
        var at = keys.IndexOf(index);
        var to = at + offset;
        if (at < 0 || to < 0 || to >= keys.Count)
        {
            return;
        }

        (_columns[keys[at]], _columns[keys[to]]) = (_columns[keys[to]], _columns[keys[at]]);
        _keys.SelectedIndex = to;
        AfterEdit();
    }

    private void ToggleSort()
    {
        if (_includedTabSelected || IndexOfSelected() is not { } index)
        {
            return;
        }

        _columns[index] = _columns[index] with { Descending = !_columns[index].Descending };
        AfterEdit();
    }

    private List<int> KeyPositions() =>
        [.. Enumerable.Range(0, _columns.Count).Where(i => !_columns[i].Included)];

    // The grids show key and included columns separately, so a grid row maps back through that filtered list
    // rather than straight onto _columns.
    private int? IndexOfSelected()
    {
        var positions = _includedTabSelected
            ? Enumerable.Range(0, _columns.Count).Where(i => _columns[i].Included).ToList()
            : KeyPositions();
        var selected = _includedTabSelected ? _included.SelectedIndex : _keys.SelectedIndex;

        return selected >= 0 && selected < positions.Count ? positions[selected] : null;
    }

    private void AfterEdit()
    {
        RefreshGrids();
        RefreshPicker();
        Revalidate();
    }

    // ── Apply ────────────────────────────────────────────────────────────────────────────────────────

    private IndexDefinition Wanted() => new()
    {
        Schema = _schema,
        Table = _table,
        Name = _name.Text?.Trim() ?? "",
        Clustered = _type.SelectedIndex == 1,
        Unique = _unique.IsChecked == true,
        Columns = [.. _columns],
        PadIndex = _original?.PadIndex ?? false,
        FillFactor = _original?.FillFactor ?? 0,
        IgnoreDupKey = _original?.IgnoreDupKey ?? false,
        StatisticsNoRecompute = _original?.StatisticsNoRecompute ?? false,
        AllowRowLocks = _original?.AllowRowLocks ?? true,
        AllowPageLocks = _original?.AllowPageLocks ?? true,
        // A new index gets the option only where the server has it; an existing one keeps what it read.
        OptimizeForSequentialKey = _original?.OptimizeForSequentialKey
            ?? (_optimizeForSequentialKeySupported ? false : null),
        Filter = _filter,
        DataSpace = _dataSpace,
        PartitionColumn = _partitionColumn
    };

    private void Revalidate()
    {
        var ready = !string.IsNullOrWhiteSpace(_name.Text) && _columns.Any(c => !c.Included);
        _ok.IsEnabled = ready;
        _script.IsEnabled = ready;
    }

    private async Task ApplyAsync()
    {
        _ok.IsEnabled = false;
        try
        {
            var statements = IndexScript.Alter(_context.Provider.Dialect, _original, Wanted());
            if (statements.Count == 0)
            {
                Close();
                return;
            }

            Report("Working…");
            foreach (var statement in statements)
            {
                await _context.Provider.ExecuteDdlAsync(_context.Profile, statement, CancellationToken.None);
            }

            Close();
        }
        catch (Exception ex)
        {
            Report(ex.Message);
            _ok.IsEnabled = true;
        }
    }

    private void ScriptToQueryTab()
    {
        try
        {
            _context.OpenQueryEditor?.Invoke(IndexScript.Script(_context.Provider.Dialect, _original, Wanted()));
        }
        catch (Exception ex)
        {
            Report(ex.Message);
        }
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────────────────────────────

    // OBJECT_ID reads its argument as text, so the name is bracket-quoted inside the parameter rather than
    // pasted in bare: a table called "my.table" would otherwise read as schema "my", and one with a bracket
    // in its name would end the quoting early. Same reason IndexStatements does it in the admin plugin.
    private string Qualified
    {
        get
        {
            var dialect = _context.Provider.Dialect;
            return string.IsNullOrEmpty(_schema)
                ? dialect.QuoteIdentifier(_table)
                : $"{dialect.QuoteIdentifier(_schema)}.{dialect.QuoteIdentifier(_table)}";
        }
    }

    private void Report(string message) => Dispatcher.UIThread.Post(() => _status.Text = message);

    private void Close() => (TopLevel.GetTopLevel(this) as Window)?.Close();

    // "nvarchar(50)", "nvarchar(max)", "decimal(18,2)", "int" — the shapes SSMS shows in this grid.
    private static string TypeName(string type, short maxLength, byte precision, byte scale) => type switch
    {
        "nvarchar" or "nchar" when maxLength == -1 => $"{type}(max)",
        "nvarchar" or "nchar" => $"{type}({maxLength / 2})",
        "varchar" or "char" or "varbinary" or "binary" when maxLength == -1 => $"{type}(max)",
        "varchar" or "char" or "varbinary" or "binary" => $"{type}({maxLength})",
        "decimal" or "numeric" => $"{type}({precision},{scale})",
        _ => type
    };
}
