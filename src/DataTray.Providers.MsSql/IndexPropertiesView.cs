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
    // Fragmentation and Extended Properties (phase 3) join these. The rail hides itself while there is only
    // one page — a rail with a single entry is chrome that cannot do anything.
    private static readonly string[] Pages = ["General", "Options", "Storage", "Filter"];

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

    // Options page. Grouped by what actually happens when OK is pressed, which is the distinction SSMS
    // renders identically and that this dialog makes visible with a pill per row.
    private readonly CheckBox _allowRowLocks = new() { Content = "Allow row locks", IsChecked = true };
    private readonly CheckBox _allowPageLocks = new() { Content = "Allow page locks", IsChecked = true };
    private readonly CheckBox _ignoreDupKey = new() { Content = "Ignore duplicate values" };
    private readonly CheckBox _autoRecompute = new() { Content = "Automatically recompute statistics", IsChecked = true };
    private readonly CheckBox _optimizeSequentialKey = new() { Content = "Optimize for sequential key" };
    private readonly CheckBox _padIndex = new() { Content = "Pad index" };
    private readonly NumericUpDown _fillFactor = new() { Minimum = 0, Maximum = 100, Increment = 5, Value = 0, Width = 120 };
    private readonly NumericUpDown _maxDop = new() { Minimum = 0, Maximum = 64, Value = 0, Width = 120 };
    private readonly CheckBox _sortInTempDb = new() { Content = "Sort results in tempdb" };
    private readonly TextBlock _optionDescription = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };

    // Storage page.
    private readonly ComboBox _dataSpacePicker = new() { Width = 260, PlaceholderText = "Default filegroup" };
    private readonly ComboBox _partitionColumnPicker = new() { Width = 260, IsEnabled = false };
    private readonly List<string> _partitionSchemes = [];

    // Filter page.
    private readonly TextBox _filterBox = new()
    {
        AcceptsReturn = true,
        MinHeight = 90,
        TextWrapping = TextWrapping.Wrap,
        PlaceholderText = "Slot > 0 AND Note IS NOT NULL"
    };

    private readonly TextBlock _filterRowCount = new() { Opacity = 0.75, TextWrapping = TextWrapping.Wrap };

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

        _built[index] ??= index switch
        {
            0 => BuildGeneral(),
            1 => BuildOptions(),
            2 => BuildStorage(),
            _ => BuildFilter()
        };

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

    // ── Options ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What pressing OK actually does with a row's value. SSMS renders all three identically,
    /// which is why people are surprised that a fill factor costs a rebuild and a MAXDOP is never saved.</summary>
    private enum Applies
    {
        /// <summary>In place, via <c>ALTER INDEX … SET</c> — no data is read or written.</summary>
        OnOk,

        /// <summary>Only by writing the index again. <c>SET</c> rejects FILLFACTOR at parse time.</summary>
        Rebuild,

        /// <summary>Steers the next rebuild and is stored nowhere, so it reads back as nothing.</summary>
        NextRebuild
    }

    private Control BuildOptions()
    {
        _ignoreDupKey.IsCheckedChanged += (_, _) => Revalidate();
        _padIndex.IsCheckedChanged += (_, _) => Revalidate();
        _fillFactor.ValueChanged += (_, _) => Revalidate();

        var page = FormBits.Page(
            new TextBlock
            {
                Text = "Each setting says when it takes effect. That distinction is invisible in SSMS, and it "
                    + "is the difference between a metadata change and reading every page of the index.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75
            },
            FormBits.Section("Locking and duplicates"),
            OptionRow(_allowRowLocks, Applies.OnOk,
                "Lets the engine take row locks on this index. Turning it off pushes contention up to page "
                + "or table level — occasionally what a hot index wants, usually not."),
            OptionRow(_allowPageLocks, Applies.OnOk,
                "Lets the engine take page locks. Off also rules out page-level lock escalation, and stops "
                + "REORGANIZE from doing anything useful."),
            OptionRow(_ignoreDupKey, Applies.OnOk,
                "On a unique index, a multi-row INSERT that hits a duplicate skips that row and keeps the "
                + "rest, instead of failing the whole statement. Only meaningful on a unique index."),

            FormBits.Section("Statistics"),
            OptionRow(_autoRecompute, Applies.OnOk,
                "Lets the engine refresh this index's statistics as the data changes. Off (STATISTICS_"
                + "NORECOMPUTE = ON) freezes them until someone runs UPDATE STATISTICS by hand."),
            OptionRow(_optimizeSequentialKey, Applies.OnOk,
                "Reduces the last-page insert contention an ever-increasing key causes. SQL Server 2019 and "
                + "later only — on an older server the option is hidden, because it does not parse there."),

            FormBits.Section("Storage density"),
            OptionRow(FormBits.Row("Fill factor (%) — 0 means the server default", _fillFactor), Applies.Rebuild,
                "How full each leaf page is packed when the index is built. Lower leaves room for inserts "
                + "and costs space and reads. Changing it rebuilds the index — ALTER INDEX … SET refuses "
                + "FILLFACTOR at parse time, so it cannot be applied any other way."),
            OptionRow(_padIndex, Applies.Rebuild,
                "Applies the fill factor to the intermediate levels of the b-tree too, not just the leaves. "
                + "Meaningless without a fill factor, and rebuilds for the same reason."),

            FormBits.Section("This rebuild only"),
            OptionRow(FormBits.Row("Maximum degree of parallelism — 0 means the server default", _maxDop),
                Applies.NextRebuild,
                "Caps the processors this one build uses. Nothing is stored: reopen the dialog and it reads "
                + "0 again, because there is no such property on an index."),
            OptionRow(_sortInTempDb, Applies.NextRebuild,
                "Sorts the intermediate results in tempdb rather than in the destination filegroup. Trades "
                + "tempdb space for a shorter build. Also stored nowhere."),

            FormBits.Section("What this row does"),
            _optionDescription);

        _optionDescription.Text = "Focus a setting to see what it does and when it takes effect.";
        return page;
    }

    private Control OptionRow(Control editor, Applies applies, string description)
    {
        editor.GotFocus += (_, _) => _optionDescription.Text = description;
        // A checkbox's own label is the row label, so there is no separate label column to fill.
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 2, 0, 2) };
        Grid.SetColumn(editor, 0);
        var pill = Pill(applies);
        Grid.SetColumn(pill, 1);
        row.Children.Add(editor);
        row.Children.Add(pill);
        return row;
    }

    private static Control Pill(Applies applies) => new Border
    {
        Background = new SolidColorBrush(Color.FromArgb(28, 128, 128, 128)),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(8, 2, 8, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            FontSize = 11,
            Opacity = 0.8,
            Text = applies switch
            {
                Applies.OnOk => "on OK",
                Applies.Rebuild => "rebuilds",
                _ => "next rebuild"
            }
        }
    };

    // ── Storage ──────────────────────────────────────────────────────────────────────────────────────

    private Control BuildStorage()
    {
        _dataSpacePicker.SelectionChanged += (_, _) =>
        {
            // A partition scheme has to name the column it partitions on; a filegroup has nothing to pick.
            var scheme = _dataSpacePicker.SelectedItem is string name && _partitionSchemes.Contains(name);
            _partitionColumnPicker.IsEnabled = scheme;
            _partitionColumnPicker.ItemsSource = scheme
                ? _columns.Where(c => !c.Included).Select(c => c.Name).ToList()
                : (IEnumerable<string>)[];
            Revalidate();
        };

        return FormBits.Page(
            new TextBlock
            {
                Text = "Where the index lives. Changing it writes the index again, in the destination.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75
            },
            FormBits.Labelled("Filegroup or partition scheme", _dataSpacePicker),
            FormBits.Labelled("Partition column", _partitionColumnPicker),
            new TextBlock
            {
                Text = "Existing partition schemes only — creating one is a separate piece of work with its "
                    + "own partition function, and not something to hide behind a dropdown here.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.65
            });
    }

    // ── Filter ───────────────────────────────────────────────────────────────────────────────────────

    private Control BuildFilter()
    {
        _filterBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                Revalidate();
            }
        };

        var check = new Button { Content = "Check rows" };
        check.Click += async (_, _) => await CheckFilterRowsAsync(check);

        // WHERE is a fixed prefix rather than something to type: typing it makes the predicate invalid, and
        // leaving it out of an empty box makes an unfiltered index look like a mistake.
        var predicate = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var where = new TextBlock
        {
            Text = "WHERE",
            FontWeight = FontWeight.SemiBold,
            Opacity = 0.7,
            Margin = new Thickness(0, 6, 10, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetColumn(where, 0);
        Grid.SetColumn(_filterBox, 1);
        predicate.Children.Add(where);
        predicate.Children.Add(_filterBox);

        return FormBits.Page(
            new TextBlock
            {
                Text = "A filtered index covers only the rows matching this predicate — smaller, cheaper to "
                    + "maintain, and only usable by queries the optimiser can prove fall inside it. Leave it "
                    + "empty for an ordinary index.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75
            },
            predicate,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { check, _filterRowCount } },
            new TextBlock
            {
                Text = "Check rows runs a COUNT over the table with this predicate. On a large table that is "
                    + "a scan, so it runs only when asked — same reason the fragmentation scan is a button.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.65
            });
    }

    private async Task CheckFilterRowsAsync(Button button)
    {
        button.IsEnabled = false;
        _filterRowCount.Text = "Counting…";
        try
        {
            var filter = _filterBox.Text?.Trim();
            var sql = $"SELECT COUNT_BIG(*) FROM {Qualified}"
                + (string.IsNullOrEmpty(filter) ? "" : $" WHERE {filter}");

            await using var connection = new SqlConnection(_context.Profile.ConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            var matched = Convert.ToInt64(await command.ExecuteScalarAsync());

            _filterRowCount.Text = string.IsNullOrEmpty(filter)
                ? $"{matched:N0} rows in the table."
                : $"{matched:N0} rows match.";
        }
        catch (Exception ex)
        {
            // The predicate is the user's own SQL, so a syntax error here is feedback, not a failure.
            _filterRowCount.Text = ex.Message;
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    // ── Load ─────────────────────────────────────────────────────────────────────────────────────────

    private async Task LoadAsync()
    {
        try
        {
            await using var connection = new SqlConnection(_context.Profile.ConnectionString);
            await connection.OpenAsync();

            await LoadTableColumnsAsync(connection);
            await LoadDataSpacesAsync(connection);
            _optimizeForSequentialKeySupported = await SupportsOptimizeForSequentialKeyAsync(connection);

            if (!_creating)
            {
                await LoadIndexAsync(connection);
            }

            Dispatcher.UIThread.Post(() =>
            {
                // Hidden rather than disabled on a pre-2019 server: there is nothing the user could do to
                // make it available, and the option does not parse there at all.
                _optimizeSequentialKey.IsVisible = _optimizeForSequentialKeySupported;
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

    // The filegroups and partition schemes an index can be built on. Both are data spaces, and SSMS offers
    // them in one dropdown, so they are read as one list with the schemes remembered separately — only they
    // need a partitioning column naming.
    private async Task LoadDataSpacesAsync(SqlConnection connection)
    {
        const string sql = """
            SELECT name, type FROM sys.data_spaces WHERE type IN ('FG', 'PS') ORDER BY type DESC, name
            """;

        var spaces = new List<string>();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var name = reader.GetString(0);
            spaces.Add(name);
            if (reader.GetString(1).Trim() == "PS")
            {
                _partitionSchemes.Add(name);
            }
        }

        Dispatcher.UIThread.Post(() => _dataSpacePicker.ItemsSource = spaces);
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
            // Stripped here rather than only for display, so the definition the dialog diffs against is the
            // same text the Filter box holds — otherwise every OK would look like a changed filter.
            _filter = reader.IsDBNull(7) ? null : IndexScript.StripOuterParentheses(reader.GetString(7));
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
            _padIndex.IsChecked = padded;
            _fillFactor.Value = fillFactor;
            _ignoreDupKey.IsChecked = ignoreDup;
            // SSMS words this the other way round from the catalog, and the positive reading is the one
            // people get right: "automatically recompute" is NOT no_recompute.
            _autoRecompute.IsChecked = !noRecompute;
            _allowRowLocks.IsChecked = rowLocks;
            _allowPageLocks.IsChecked = pageLocks;
            _optimizeSequentialKey.IsChecked = optimize == true;
            _filterBox.Text = _filter;
            _dataSpacePicker.SelectedItem = _dataSpace;
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

    private IndexDefinition Wanted()
    {
        var space = _dataSpacePicker.SelectedItem as string;
        var partitioned = space is not null && _partitionSchemes.Contains(space);

        return new IndexDefinition
        {
            Schema = _schema,
            Table = _table,
            Name = _name.Text?.Trim() ?? "",
            Clustered = _type.SelectedIndex == 1,
            Unique = _unique.IsChecked == true,
            Columns = [.. _columns],
            PadIndex = _padIndex.IsChecked == true,
            FillFactor = (int)(_fillFactor.Value ?? 0),
            IgnoreDupKey = _ignoreDupKey.IsChecked == true,
            StatisticsNoRecompute = _autoRecompute.IsChecked != true,
            AllowRowLocks = _allowRowLocks.IsChecked == true,
            AllowPageLocks = _allowPageLocks.IsChecked == true,
            // Null where the server cannot parse the option at all, which is not the same as OFF.
            OptimizeForSequentialKey = _optimizeForSequentialKeySupported
                ? _optimizeSequentialKey.IsChecked == true
                : null,
            Filter = string.IsNullOrWhiteSpace(_filterBox.Text) ? null : _filterBox.Text.Trim(),
            DataSpace = space,
            PartitionColumn = partitioned ? _partitionColumnPicker.SelectedItem as string : null
        };
    }

    private RebuildOptions Rebuild() =>
        new((int)(_maxDop.Value ?? 0), _sortInTempDb.IsChecked == true);

    private void Revalidate()
    {
        // Disabled with a reason rather than hidden: a checkbox that vanishes when you clear Unique looks
        // like a bug, where a greyed one with an explanation is an answer.
        var unique = _unique.IsChecked == true;
        _ignoreDupKey.IsEnabled = unique;
        ToolTip.SetTip(_ignoreDupKey, unique ? null : "Only a unique index can ignore duplicate values.");

        var padded = _padIndex.IsChecked == true;
        var hasFillFactor = (_fillFactor.Value ?? 0) > 0;
        ToolTip.SetTip(_padIndex, padded && !hasFillFactor
            ? "Pad index does nothing without a fill factor — it applies the same density to the b-tree's "
                + "intermediate levels."
            : null);

        var ready = !string.IsNullOrWhiteSpace(_name.Text) && _columns.Any(c => !c.Included);
        _ok.IsEnabled = ready;
        _script.IsEnabled = ready;
    }

    private async Task ApplyAsync()
    {
        _ok.IsEnabled = false;
        try
        {
            var statements = IndexScript.Alter(_context.Provider.Dialect, _original, Wanted(), Rebuild());
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
            _context.OpenQueryEditor?.Invoke(
                IndexScript.Script(_context.Provider.Dialect, _original, Wanted(), Rebuild()));
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
