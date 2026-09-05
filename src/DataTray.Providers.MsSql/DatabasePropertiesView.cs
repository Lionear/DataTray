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
/// Route-B info view (Notes §4.4, third capability): SQL Server's "Database Properties" dialog. Mirrors
/// SSMS' layout — a page rail on the left and a read-only detail area on the right — and reproduces the
/// fields SSMS surfaces on each page (General, Files, Filegroups, Options, Change Tracking, Permissions,
/// Extended Properties, Query Store). Each page loads its own data lazily the first time it is shown, so
/// opening the dialog only runs the General queries. Built entirely in code (no XAML, no DataGrid — the
/// plugin only references Avalonia core) so it stays self-contained across the ALC boundary, same as
/// <see cref="MsSqlAdvancedView"/>.
/// </summary>
public sealed class DatabasePropertiesView : UserControl
{
    private static readonly string[] Pages =
    [
        "General", "Files", "Filegroups", "Options", "Change Tracking", "Permissions", "Extended Properties",
        "Query Store", "Configurations", "Transaction Log Shipping"
    ];

    private readonly NodeInfoContext _context;
    private readonly string _database;

    // A ContentControl, not a ScrollViewer. A ScrollViewer measures its content at whatever height the
    // content wants, so a page whose body is a grid could never fill the dialog — a six-row table sat in
    // the top corner with the rest of the page empty under it, which is what made these pages read as
    // unfinished. Each page now says for itself whether it scrolls (a long list of settings) or fills
    // (a grid, which should own its area the way SSMS' do).
    private readonly ContentControl _host = new();
    private readonly Control?[] _built = new Control?[Pages.Length];

    public DatabasePropertiesView(NodeInfoContext context)
    {
        _context = context;
        _database = context.Node.Name;

        var rail = new ListBox
        {
            Width = 185,
            ItemsSource = Pages,
            SelectedIndex = 0,
            Background = Brushes.Transparent
        };
        // The longest label ("Extended Properties") is wider than a narrow rail; without this the ListBox
        // scrolls horizontally and clips the labels' left edge. Disabled = clip cleanly, never offset.
        ScrollViewer.SetHorizontalScrollBarVisibility(rail, ScrollBarVisibility.Disabled);
        rail.SelectionChanged += (_, _) => ShowPage(rail.SelectedIndex);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(rail, 0);
        Grid.SetColumn(_host, 1);
        body.Children.Add(rail);
        body.Children.Add(_host);

        // The dialog writes now, so it owns OK/Cancel and the host leaves off its Close row
        // (ICustomNodeInfoUi.InfoViewOwnsActionBar). Everything is committed here rather than per page:
        // several pages can change at once, and a page that saved itself on the way past would leave the
        // dialog half-applied when the next one failed.
        var cancel = new Button { Content = "Cancel", MinWidth = 96 };
        cancel.Click += (_, _) => Close();
        _ok.Click += async (_, _) => await ApplyAsync();

        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 12, 0, 0)
        };
        _status.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_status, 0);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _ok, cancel }
        };
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
    }

    private readonly Button _ok = new() { Content = "OK", MinWidth = 96, Classes = { "Accent" } };

    // Centred against the buttons it shares the footer with; Label() only carries the colour.
    private readonly TextBlock _status = FormBits.Label("");

    // A standalone toggle keeps its caption, unlike the ones in a label/value row: there is no label column
    // out here to say what it does. Same shape the host's settings window uses.
    private readonly ToggleSwitch _rollbackImmediate = new()
    {
        OnContent = "Disconnect other sessions (WITH ROLLBACK IMMEDIATE)",
        OffContent = "Disconnect other sessions (WITH ROLLBACK IMMEDIATE)"
    };

    // The Options page and its state as loaded. Null until the page has been opened once — an untouched
    // page has nothing to write, which is also why opening the dialog and pressing OK does nothing.
    private PropPage? _options;
    private IReadOnlyDictionary<string, string>? _optionsAsLoaded;

    private async Task ApplyAsync()
    {
        _ok.IsEnabled = false;
        try
        {
            var statements = PendingStatements();
            if (statements.Count == 0)
            {
                Close();
                return;
            }

            Report($"Applying {statements.Count} change(s)…");
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

    /// <summary>Everything OK will run, in page order. A page never opened contributes nothing, because its
    /// editors were never built and there is nothing to have changed.</summary>
    private IReadOnlyList<string> PendingStatements()
    {
        var dialect = _context.Provider.Dialect;
        var statements = new List<string>();

        if (_options is not null && _optionsAsLoaded is not null)
        {
            statements.AddRange(DatabaseOptionWriter.Alter(
                dialect, _database, _optionsAsLoaded, _options.Snapshot(), _rollbackImmediate.IsChecked == true));
        }

        statements.AddRange(DatabaseOptionWriter.ExtendedProperties(
            _originalExtendedProperties, _extendedProperties));

        statements.AddRange(_fileChanges.Values);

        return statements;
    }

    private void Report(string message) => Dispatcher.UIThread.Post(() => _status.Text = message);

    private void Close() => (TopLevel.GetTopLevel(this) as Window)?.Close();

    // Build a page the first time it is selected (kicking off its own load), then cache it.
    private void ShowPage(int index)
    {
        if (index < 0)
        {
            return;
        }

        if (_built[index] is null)
        {
            var page = index switch
            {
                0 => BuildGeneral(),
                1 => BuildFiles(),
                2 => BuildFilegroups(),
                3 => BuildOptions(),
                4 => BuildChangeTracking(),
                5 => BuildPermissions(),
                6 => BuildExtendedProperties(),
                7 => BuildQueryStore(),
                8 => BuildConfigurations(),
                9 => BuildLogShipping(),
                _ => new StackPanel()
            };
            // No ScrollViewer here — the host dialog already wraps this whole view in one; nesting a second
            // would leave the inner content unbounded and never scroll.
            // Breathing room on all four sides, and clear of the scrollbar on the right — the page used to
            // run straight into it. A Grid rather than a StackPanel so a page that wants the height gets it.
            _built[index] = new Grid { Margin = new Thickness(20, 4, 22, 18), Children = { page } };
        }

        _host.Content = _built[index];
    }

    // ── General ──────────────────────────────────────────────────────────────────────────────────────

    private Control BuildGeneral()
    {
        var p = new PropPage();
        p.Section("Backup");
        p.Row("Last Database Backup", "lastBackup");
        p.Row("Last Database Log Backup", "lastLogBackup");
        p.Section("Database");
        p.Row("Name", "name");
        p.Row("Status", "status");
        p.Row("Owner", "owner");
        p.Row("Date Created", "created");
        p.Row("Size", "size");
        p.Row("Space Available", "free");
        p.Row("Number of Users", "users");
        p.Row("Memory Allocated To Memory Optimized Objects", "xtpAlloc");
        p.Row("Memory Used By Memory Optimized Objects", "xtpUsed");
        p.Section("Maintenance");
        p.Row("Collation", "collation");
        p.Row("Transaction Log Shipping", "logShipping");

        p.Values["name"].Text = _database;
        _ = LoadGeneralAsync(p);
        return Scrolls(p.Stack);
    }

    private async Task LoadGeneralAsync(PropPage p)
    {
        try
        {
            await using var connection = await OpenAsync();

            await RunAsync(connection,
                """
                SELECT d.state_desc, SUSER_SNAME(d.owner_sid), d.create_date, d.collation_name
                FROM sys.databases d WHERE d.name = @db
                """,
                cmd => cmd.Parameters.AddWithValue("@db", _database),
                reader =>
                {
                    p.Set("status", Str(reader, 0));
                    p.Set("owner", Str(reader, 1));
                    p.Set("created", reader.IsDBNull(2) ? null : reader.GetDateTime(2).ToString("g"));
                    p.Set("collation", Str(reader, 3));
                });

            await RunAsync(connection,
                """
                SELECT
                    CAST(SUM(CAST(size AS bigint)) * 8.0 / 1024 AS decimal(18,2)),
                    CAST(SUM(CAST(size - FILEPROPERTY(name, 'SpaceUsed') AS bigint)) * 8.0 / 1024 AS decimal(18,2))
                FROM sys.database_files WHERE type IN (0, 1)
                """,
                _ => { },
                reader =>
                {
                    p.Set("size", reader.IsDBNull(0) ? null : $"{reader.GetDecimal(0):N2} MB");
                    p.Set("free", reader.IsDBNull(1) ? null : $"{reader.GetDecimal(1):N2} MB");
                });

            await RunAsync(connection,
                "SELECT COUNT(*) FROM sys.database_principals WHERE type IN ('S', 'U', 'G') AND principal_id > 4",
                _ => { },
                reader => p.Set("users", reader.GetInt32(0).ToString()));

            await TryAsync(() => RunAsync(connection,
                    "SELECT CAST(ISNULL(SUM(allocated_bytes), 0) / 1024.0 / 1024 AS decimal(18,2)), CAST(ISNULL(SUM(used_bytes), 0) / 1024.0 / 1024 AS decimal(18,2)) FROM sys.dm_db_xtp_table_memory_stats",
                    _ => { },
                    reader =>
                    {
                        p.Set("xtpAlloc", $"{reader.GetDecimal(0):N2} MB");
                        p.Set("xtpUsed", $"{reader.GetDecimal(1):N2} MB");
                    }),
                () => { p.Set("xtpAlloc", "0.00 MB"); p.Set("xtpUsed", "0.00 MB"); });

            await TryAsync(() => RunAsync(connection,
                    """
                    SELECT type, MAX(backup_finish_date)
                    FROM msdb.dbo.backupset WHERE database_name = @db AND type IN ('D', 'L')
                    GROUP BY type
                    """,
                    cmd => cmd.Parameters.AddWithValue("@db", _database),
                    reader =>
                    {
                        var finish = reader.IsDBNull(1) ? (DateTime?)null : reader.GetDateTime(1);
                        var text = finish?.ToString("g") ?? "None";
                        if (reader.GetString(0) == "D") p.Set("lastBackup", text); else p.Set("lastLogBackup", text);
                    }),
                () => { });

            // The same question the Transaction Log Shipping page answers in full, as the one-word summary
            // SSMS puts here. In msdb, so it needs its own try like the backup history above.
            await TryAsync(() => RunAsync(connection,
                    """
                    SELECT CASE WHEN EXISTS (SELECT 1 FROM msdb.dbo.log_shipping_primary_databases
                                             WHERE primary_database = @db)
                                  OR EXISTS (SELECT 1 FROM msdb.dbo.log_shipping_secondary_databases
                                             WHERE secondary_database = @db)
                                THEN 1 ELSE 0 END
                    """,
                    cmd => cmd.Parameters.AddWithValue("@db", _database),
                    reader => p.Set("logShipping", YesNo(reader.GetInt32(0) == 1))),
                () => p.Set("logShipping", "Unknown"));

            if (p.Values["lastBackup"].Text is "…") p.Set("lastBackup", "None");
            if (p.Values["lastLogBackup"].Text is "…") p.Set("lastLogBackup", "None");
        }
        catch (Exception ex)
        {
            p.Fail(ex);
        }
    }

    // ── Files ────────────────────────────────────────────────────────────────────────────────────────

    // One pending MODIFY FILE per file, keyed by logical name so editing the same file twice replaces the
    // statement instead of queueing two.
    private readonly Dictionary<string, string> _fileChanges = new(StringComparer.OrdinalIgnoreCase);

    private Control BuildFiles()
    {
        var table = new Table(
            // Path takes the leftover width — it is the column that has to be read in full and the only one
            // with no natural length. The other six are sized to their content so none of that is wasted,
            // and every cell carries its own value as a tooltip for whatever still does not fit.
            ["Logical Name", "File Type", "Filegroup", "Initial Size (MB)", "Autogrowth / Maxsize", "File Name", "Path"],
            [130, 80, 90, 105, 165, 140, 0]);
        _ = LoadFilesAsync(table);

        var owner = new PropPage();
        owner.Row("Database name", "dbName");
        owner.Row("Owner", "dbOwner");
        owner.Set("dbName", _database);
        _ = LoadFileOwnerAsync(owner);

        // The grid takes the height between the header above it and the autogrowth editor below.
        var page = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        Control[] parts = [owner.Stack, Header("Database files"), table.Control, BuildAutogrowthEditor()];
        for (var i = 0; i < parts.Length; i++)
        {
            Grid.SetRow(parts[i], i);
            page.Children.Add(parts[i]);
        }

        return page;
    }

    private async Task LoadFileOwnerAsync(PropPage p)
    {
        try
        {
            await using var connection = await OpenAsync();
            await RunAsync(connection,
                "SELECT SUSER_SNAME(owner_sid) FROM sys.databases WHERE name = @db",
                cmd => cmd.Parameters.AddWithValue("@db", _database),
                reader => p.Set("dbOwner", Str(reader, 0)));
        }
        catch (Exception ex)
        {
            p.Fail(ex);
        }
    }

    /// <summary>
    /// SSMS opens a small "Change Autogrowth" modal from a "…" button inside the grid cell. The grid here is
    /// a <see cref="Table"/>, which holds text and nothing else, so the editor sits under the grid and names
    /// the file it acts on instead.
    /// </summary>
    /// <remarks>
    /// ponytail: an editor below the grid rather than a control in the cell. The in-cell version needs a
    /// grid widget that can host controls — worth building when a second page wants one, not for this.
    /// </remarks>
    private Control BuildAutogrowthEditor()
    {
        var file = new ComboBox { Width = 200, PlaceholderText = "File" };
        var growth = new NumericUpDown { Minimum = 0, Maximum = 100_000, Value = 64, Width = 130 };
        var unit = new ComboBox { ItemsSource = new[] { "In megabytes", "In percent" }, SelectedIndex = 0, Width = 150 };
        var maxKind = new ComboBox
        {
            ItemsSource = new[] { "Unlimited", "Limited to (MB)" },
            SelectedIndex = 0,
            Width = 170
        };

        var maxSize = new NumericUpDown { Minimum = 1, Maximum = 100_000_000, Value = 100, Width = 150, IsEnabled = false };
        maxKind.SelectionChanged += (_, _) => maxSize.IsEnabled = maxKind.SelectedIndex == 1;

        var status = FormBits.Label("");

        var apply = new Button { Content = "Stage change" };
        apply.Click += (_, _) =>
        {
            if (file.SelectedItem is not string name)
            {
                status.Text = "Pick a file first.";
                return;
            }

            _fileChanges[name] = DatabaseOptionWriter.ModifyFile(
                _context.Provider.Dialect, _database, name,
                (int)(growth.Value ?? 0),
                unit.SelectedIndex == 1,
                maxKind.SelectedIndex == 1 ? (int)(maxSize.Value ?? 0) : null);

            status.Text = $"{_fileChanges.Count} file change(s) will run when you press OK.";
        };

        _ = LoadFileNamesAsync(file);

        return Scrolls(new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Header("Change autogrowth"),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { file, growth, unit, maxKind, maxSize, apply }
                },
                status
            }
        });
    }

    private async Task LoadFileNamesAsync(ComboBox picker)
    {
        try
        {
            await using var connection = await OpenAsync();
            var names = new List<string>();
            await RunAsync(connection,
                "SELECT name FROM sys.database_files ORDER BY type, file_id",
                _ => { },
                reader => names.Add(reader.GetString(0)));
            Dispatcher.UIThread.Post(() => picker.ItemsSource = names);
        }
        catch
        {
            // The grid above shows the same failure; a second copy of it here is noise.
        }
    }

    private async Task LoadFilesAsync(Table table)
    {
        try
        {
            await using var connection = await OpenAsync();
            var rows = new List<string[]>();
            await RunAsync(connection,
                """
                SELECT df.name,
                       df.type,
                       ISNULL(fg.name, ''),
                       CAST(df.size * 8.0 / 1024 AS decimal(18,2)),
                       df.is_percent_growth, df.growth, df.max_size,
                       df.physical_name
                FROM sys.database_files df
                LEFT JOIN sys.filegroups fg ON df.data_space_id = fg.data_space_id
                ORDER BY df.type, df.file_id
                """,
                _ => { },
                reader =>
                {
                    var (dir, file) = SplitPath(reader.GetString(7));
                    rows.Add([
                        reader.GetString(0),
                        FileType(reader.GetByte(1)),
                        reader.GetString(2),
                        $"{reader.GetDecimal(3):N2}",
                        Autogrowth(reader.GetBoolean(4), reader.GetInt32(5), reader.GetInt32(6)),
                        file,
                        dir
                    ]);
                });
            table.Fill(rows);
        }
        catch (Exception ex)
        {
            table.Fail(ex);
        }
    }

    // ── Filegroups ───────────────────────────────────────────────────────────────────────────────────

    private Control BuildFilegroups()
    {
        // SSMS shows three grids here. The old single one filtered on type = 'FG', so a database with
        // FILESTREAM or memory-optimized data showed nothing about it at all — not an empty section, no
        // section.
        var rows = new Table(["Name", "Files", "Read-Only", "Default", "Autogrow All Files"], [0, 80, 100, 90, 140], 190);
        var filestream = new Table(["Name", "Files", "Read-Only", "Default"], [0, 80, 100, 90], 130);
        var memoryOptimized = new Table(["Name", "Files"], [0, 80], 130);

        _ = LoadFilegroupsAsync(rows, filestream, memoryOptimized);

        return Scrolls(new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Header("Rows"),
                rows.Control,
                Header("FILESTREAM"),
                filestream.Control,
                Header("MEMORY OPTIMIZED DATA"),
                memoryOptimized.Control
            }
        });
    }

    /// <summary>A page that is a list of settings: it scrolls, and is as tall as it needs to be.</summary>
    private static Control Scrolls(Control content) => new ScrollViewer
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Content = content
    };

    /// <summary>A page whose subject is a grid. The grid takes the page's height and anything else — a note,
    /// an editor — sits under it at its natural size, so the page has no dead area under a short table.</summary>
    private static Control Fills(Control grid, params Control[] below)
    {
        var rows = new Grid
        {
            RowDefinitions = new RowDefinitions("*" + string.Concat(below.Select(_ => ",Auto")))
        };
        Grid.SetRow(grid, 0);
        rows.Children.Add(grid);
        for (var i = 0; i < below.Length; i++)
        {
            Grid.SetRow(below[i], i + 1);
            rows.Children.Add(below[i]);
        }

        return rows;
    }

    private static TextBlock Header(string text)
    {
        var block = FormBits.Section(text);
        block.Margin = new Thickness(0, 16, 0, 6);
        return block;
    }

    /// <summary>A closing note under a page's content — the quietest text on the page.</summary>
    private static TextBlock Note(string text)
    {
        var block = FormBits.Hint(text);
        block.Margin = new Thickness(0, 10, 0, 0);
        return block;
    }

    private async Task LoadFilegroupsAsync(Table rows, Table filestream, Table memoryOptimized)
    {
        try
        {
            await using var connection = await OpenAsync();
            List<string[]> row = [], fs = [], mo = [];

            var autogrowAll = true;
            void Read(SqlDataReader reader)
            {
                var name = reader.GetString(0);
                var files = reader.GetInt32(2).ToString();
                switch (reader.GetString(1).Trim())
                {
                    case "FG":
                        row.Add([name, files, Tick(reader.GetBoolean(3)), Tick(reader.GetBoolean(4)),
                            autogrowAll ? Tick(reader.GetBoolean(5)) : "—"]);
                        break;
                    case "FD":
                        fs.Add([name, files, Tick(reader.GetBoolean(3)), Tick(reader.GetBoolean(4))]);
                        break;
                    case "FX":
                        mo.Add([name, files]);
                        break;
                }
            }

            // is_autogrow_all_files arrived in SQL Server 2016, so the query runs with it and is retried
            // without it if the server has never heard of it. Run-and-retry rather than a metadata probe:
            // sys.filegroups is a *catalog view*, and a catalog view's columns live in sys.system_columns,
            // not sys.columns — so COL_LENGTH answers NULL for a column that is right there, which is
            // exactly how this page ended up broken on a 2017 server. Asking the server to run the statement
            // is the only test that cannot be wrong about what the server will run.
            try
            {
                await RunAsync(connection, FilegroupQuery(withAutogrowAll: true), _ => { }, Read);
            }
            catch (SqlException)
            {
                autogrowAll = false;
                row.Clear();
                fs.Clear();
                mo.Clear();
                await RunAsync(connection, FilegroupQuery(withAutogrowAll: false), _ => { }, Read);
            }

            rows.Fill(row);
            filestream.Fill(fs);
            memoryOptimized.Fill(mo);
        }
        catch (Exception ex)
        {
            rows.Fail(ex);
            filestream.Fail(ex);
            memoryOptimized.Fail(ex);
        }
    }

    /// <summary>
    /// Every filegroup with its file count. A correlated subquery rather than a LEFT JOIN and a GROUP BY:
    /// grouping by a catalog view's columns hits "Each GROUP BY expression must contain at least one column
    /// that is not an outer join column", because sys.filegroups is itself defined over an outer join. The
    /// subquery form has no GROUP BY to get wrong and reads as what it is — a count per filegroup.
    /// </summary>
    private static string FilegroupQuery(bool withAutogrowAll) => $"""
        SELECT fg.name, fg.type,
               (SELECT COUNT(*) FROM sys.database_files AS df WHERE df.data_space_id = fg.data_space_id),
               fg.is_read_only, fg.is_default{(withAutogrowAll ? ", fg.is_autogrow_all_files" : "")}
        FROM sys.filegroups AS fg
        ORDER BY fg.name
        """;

    // ── Options ──────────────────────────────────────────────────────────────────────────────────────

    private Control BuildOptions()
    {
        var p = new PropPage();
        p.Section("General");
        p.Row("Collation", "collation");
        Choice(p, "Recovery Model", "recovery", ["SIMPLE", "BULK_LOGGED", "FULL"]);
        p.Row("Compatibility Level", "compat");
        p.Row("Containment Type", "containment");
        p.Section("Automatic");
        Flag(p, "Auto Close", "autoClose");
        Flag(p, "Auto Create Statistics", "autoCreateStats");
        // Read-only on purpose: INCREMENTAL is an argument of AUTO_CREATE_STATISTICS, not an option of its
        // own, so making it a second checkbox would let the two rows disagree about one statement.
        p.Row("Auto Create Incremental Statistics", "autoCreateStatsInc");
        Flag(p, "Auto Shrink", "autoShrink");
        Flag(p, "Auto Update Statistics", "autoUpdateStats");
        Flag(p, "Auto Update Statistics Asynchronously", "autoUpdateStatsAsync");
        p.Section("Cursor");
        Flag(p, "Close Cursor on Commit Enabled", "cursorClose");
        Choice(p, "Default Cursor", "cursorDefault", ["GLOBAL", "LOCAL"]);
        p.Section("Recovery");
        Choice(p, "Page Verify", "pageVerify", ["CHECKSUM", "TORN_PAGE_DETECTION", "NONE"]);
        Number(p, "Target Recovery Time (Seconds)", "targetRecovery");
        p.Section("Miscellaneous");
        Flag(p, "ANSI NULL Default", "ansiNullDefault");
        Flag(p, "ANSI NULLS Enabled", "ansiNulls");
        Flag(p, "ANSI Padding Enabled", "ansiPadding");
        Flag(p, "ANSI Warnings Enabled", "ansiWarnings");
        Flag(p, "Arithmetic Abort Enabled", "arithAbort");
        Flag(p, "Concatenate Null Yields Null", "concatNull");
        Flag(p, "Numeric Round-Abort", "numericRoundAbort");
        Flag(p, "Quoted Identifiers Enabled", "quotedIdentifier");
        Flag(p, "Recursive Triggers Enabled", "recursiveTriggers");
        Flag(p, "Trustworthy", "trustworthy");
        Flag(p, "Date Correlation Optimization Enabled", "dateCorrelation");
        Choice(p, "Parameterization", "parameterization", ["SIMPLE", "FORCED"]);
        Choice(p, "Delayed Durability", "delayedDurability", ["DISABLED", "ALLOWED", "FORCED"]);
        p.Section("Service Broker");
        Choice(p, "Broker Enabled", "broker", ["ENABLE_BROKER", "DISABLE_BROKER"], ["True", "False"]);
        Flag(p, "Honor Broker Priority", "brokerPriority");
        p.Row("Service Broker Identifier", "brokerGuid");
        p.Section("FILESTREAM");
        p.Row("FILESTREAM Directory Name", "filestreamDirectory");
        p.Row("FILESTREAM Non-Transacted Access", "filestreamAccess");
        p.Section("State");
        p.Row("Database State", "state");
        Choice(p, "Database Read-Only", "readOnly", ["READ_WRITE", "READ_ONLY"], ["False", "True"]);
        Choice(p, "Restrict Access", "userAccess", ["MULTI_USER", "SINGLE_USER", "RESTRICTED_USER"]);
        p.Row("Encryption Enabled", "encrypted");
        Flag(p, "Allow Snapshot Isolation", "snapshotIso");
        Flag(p, "Is Read Committed Snapshot On", "rcsi");

        _options = p;
        _ = LoadOptionsAsync(p);

        return Scrolls(new StackPanel
        {
            Spacing = 4,
            Children =
            {
                p.Stack,
                Header("Changing the last four needs everyone else out"),
                _rollbackImmediate,
                FormBits.Hint(
                    "Read-only, restrict access, read-committed snapshot and the broker cannot be changed "
                    + "while other sessions are connected. SQL Server does not refuse — it waits, "
                    + "indefinitely, which looks exactly like a hung application. Turning this on adds WITH "
                    + "ROLLBACK IMMEDIATE, which disconnects those sessions and rolls back whatever they "
                    + "were doing. Left off, those four rows are not written at all.")
            }
        });
    }

    // ── Option editors ───────────────────────────────────────────────────────────────────────────────
    //
    // Each editor reads back the value in the vocabulary ALTER DATABASE uses ("ON", "FULL", "READ_ONLY"),
    // not the one the page displays, so the before/after snapshot the writer diffs needs no translation.

    private static void Flag(PropPage page, string label, string key)
    {
        var toggle = FormBits.Toggle();
        page.Edit(label, key, toggle,
            text => toggle.IsChecked = text is "True" or "ON",
            () => toggle.IsChecked == true ? "ON" : "OFF");
    }

    /// <param name="values">What ALTER DATABASE is given.</param>
    /// <param name="display">What the catalog reads back and the user sees, when it differs — the broker is
    /// a bit in sys.databases and a verb in the statement, and read-only is the same the other way round.</param>
    private static void Choice(PropPage page, string label, string key, string[] values, string[]? display = null)
    {
        var shown = display ?? [.. values.Select(Titled)];
        var box = new ComboBox { ItemsSource = shown, Width = 220 };
        page.Edit(label, key, box,
            text =>
            {
                var i = Array.FindIndex(shown, s => string.Equals(s, text, StringComparison.OrdinalIgnoreCase));
                box.SelectedIndex = i < 0 ? 0 : i;
            },
            () => values[Math.Max(box.SelectedIndex, 0)]);
    }

    private static void Number(PropPage page, string label, string key)
    {
        var box = new NumericUpDown { Minimum = 0, Maximum = 3600, Width = 140 };
        page.Edit(label, key, box,
            text => box.Value = int.TryParse(text, out var v) ? v : 0,
            () => ((int)(box.Value ?? 0)).ToString());
    }

    private async Task LoadOptionsAsync(PropPage p)
    {
        try
        {
            await using var connection = await OpenAsync();
            await RunAsync(connection,
                """
                SELECT collation_name, recovery_model_desc, compatibility_level, containment_desc,
                       is_auto_close_on, is_auto_create_stats_on, is_auto_create_stats_incremental_on,
                       is_auto_shrink_on, is_auto_update_stats_on, is_auto_update_stats_async_on,
                       page_verify_option_desc, is_read_only, user_access_desc, is_encrypted,
                       is_broker_enabled, snapshot_isolation_state_desc, is_read_committed_snapshot_on,
                       is_cursor_close_on_commit_on, is_local_cursor_default, target_recovery_time_in_seconds,
                       is_ansi_null_default_on, is_ansi_nulls_on, is_ansi_padding_on, is_ansi_warnings_on,
                       is_arithabort_on, is_concat_null_yields_null_on, is_numeric_roundabort_on,
                       is_quoted_identifier_on, is_recursive_triggers_on, is_trustworthy_on,
                       is_date_correlation_on, is_parameterization_forced, delayed_durability_desc,
                       is_honor_broker_priority_on, service_broker_guid, state_desc
                FROM sys.databases WHERE name = @db
                """,
                cmd => cmd.Parameters.AddWithValue("@db", _database),
                reader =>
                {
                    p.Set("collation", Str(reader, 0));
                    p.Set("recovery", Titled(Str(reader, 1)));
                    p.Set("compat", CompatLevel(reader.GetByte(2)));
                    p.Set("containment", Titled(Str(reader, 3)));
                    p.Set("autoClose", YesNo(reader.GetBoolean(4)));
                    p.Set("autoCreateStats", YesNo(reader.GetBoolean(5)));
                    p.Set("autoCreateStatsInc", YesNo(reader.GetBoolean(6)));
                    p.Set("autoShrink", YesNo(reader.GetBoolean(7)));
                    p.Set("autoUpdateStats", YesNo(reader.GetBoolean(8)));
                    p.Set("autoUpdateStatsAsync", YesNo(reader.GetBoolean(9)));
                    p.Set("pageVerify", Titled(Str(reader, 10)));
                    p.Set("readOnly", YesNo(reader.GetBoolean(11)));
                    p.Set("userAccess", Titled(Str(reader, 12)));
                    p.Set("encrypted", YesNo(reader.GetBoolean(13)));
                    p.Set("broker", YesNo(reader.GetBoolean(14)));
                    p.Set("snapshotIso", Titled(Str(reader, 15)));
                    p.Set("rcsi", YesNo(reader.GetBoolean(16)));
                    p.Set("cursorClose", YesNo(reader.GetBoolean(17)));
                    p.Set("cursorDefault", reader.GetBoolean(18) ? "LOCAL" : "GLOBAL");
                    p.Set("targetRecovery", reader.GetInt32(19).ToString());
                    p.Set("ansiNullDefault", YesNo(reader.GetBoolean(20)));
                    p.Set("ansiNulls", YesNo(reader.GetBoolean(21)));
                    p.Set("ansiPadding", YesNo(reader.GetBoolean(22)));
                    p.Set("ansiWarnings", YesNo(reader.GetBoolean(23)));
                    p.Set("arithAbort", YesNo(reader.GetBoolean(24)));
                    p.Set("concatNull", YesNo(reader.GetBoolean(25)));
                    p.Set("numericRoundAbort", YesNo(reader.GetBoolean(26)));
                    p.Set("quotedIdentifier", YesNo(reader.GetBoolean(27)));
                    p.Set("recursiveTriggers", YesNo(reader.GetBoolean(28)));
                    p.Set("trustworthy", YesNo(reader.GetBoolean(29)));
                    p.Set("dateCorrelation", YesNo(reader.GetBoolean(30)));
                    // SSMS words this as SIMPLE / FORCED rather than as a yes/no.
                    p.Set("parameterization", reader.GetBoolean(31) ? "Forced" : "Simple");
                    p.Set("delayedDurability", Titled(Str(reader, 32)));
                    p.Set("brokerPriority", YesNo(reader.GetBoolean(33)));
                    p.Set("brokerGuid", reader.IsDBNull(34) ? "—" : reader.GetGuid(34).ToString());
                    p.Set("state", Titled(Str(reader, 35)));
                });

            await LoadFilestreamOptionsAsync(connection, p);

            // After the loaded values have reached the editors, not before: Set posts to the UI thread, so
            // snapshotting here would capture the defaults and make every row look changed.
            await Dispatcher.UIThread.InvokeAsync(() => _optionsAsLoaded = p.Snapshot());
        }
        catch (Exception ex)
        {
            p.Fail(ex);
        }
    }

    // ── Change Tracking ──────────────────────────────────────────────────────────────────────────────

    private Control BuildChangeTracking()
    {
        var p = new PropPage();
        p.Section("Change Tracking");
        p.Row("Change Tracking", "enabled");
        p.Row("Retention Period", "retention");
        p.Row("Retention Period Units", "retentionUnits");
        p.Row("Auto Cleanup", "autoCleanup");

        _ = LoadChangeTrackingAsync(p);
        return Scrolls(p.Stack);
    }

    private async Task LoadChangeTrackingAsync(PropPage p)
    {
        try
        {
            await using var connection = await OpenAsync();
            var found = false;
            await RunAsync(connection,
                """
                SELECT is_auto_cleanup_on, retention_period, retention_period_units_desc
                FROM sys.change_tracking_databases WHERE database_id = DB_ID(@db)
                """,
                cmd => cmd.Parameters.AddWithValue("@db", _database),
                reader =>
                {
                    found = true;
                    p.Set("enabled", "True");
                    p.Set("autoCleanup", YesNo(reader.GetBoolean(0)));
                    p.Set("retention", reader.GetInt32(1).ToString());
                    p.Set("retentionUnits", Titled(Str(reader, 2)));
                });

            if (!found)
            {
                p.Set("enabled", "False");
                p.Set("retention", "—");
                p.Set("retentionUnits", "—");
                p.Set("autoCleanup", "—");
            }
        }
        catch (Exception ex)
        {
            p.Fail(ex);
        }
    }

    // ── Permissions ──────────────────────────────────────────────────────────────────────────────────

    private Control BuildPermissions()
    {
        // Permission takes the leftover width: the longest names here run to about forty characters
        // ("VIEW ANY COLUMN ENCRYPTION KEY DEFINITION"), and at a fixed 210 every one of them wrapped to two
        // lines, so a page of mostly short names had a ragged left edge down the State column.
        var table = new Table(["Grantee", "Grantor", "Permission", "State"], [170, 170, 0, 90]);
        _ = LoadPermissionsAsync(table);

        return Fills(
            table.Control,
            Note("Permissions on the database itself, which is what this dialog is about. Grants on "
                + "tables, views and other objects live with those objects."));
    }

    private async Task LoadPermissionsAsync(Table table)
    {
        try
        {
            await using var connection = await OpenAsync();
            var rows = new List<string[]>();
            // class = 0 is the database securable, which is the scope of this dialog and of SSMS's page.
            // Without it every grant on every table came back too, and since the securable's own name was
            // never resolved from major_id, dozens of rows rendered identically while meaning different
            // objects — the page was not merely unorganised, it was ambiguous.
            await RunAsync(connection,
                """
                SELECT grantee.name, ISNULL(grantor.name, ''), dp.permission_name, dp.state_desc
                FROM sys.database_permissions dp
                JOIN sys.database_principals grantee ON dp.grantee_principal_id = grantee.principal_id
                LEFT JOIN sys.database_principals grantor ON dp.grantor_principal_id = grantor.principal_id
                WHERE dp.class = 0
                ORDER BY grantee.name, dp.permission_name
                """,
                _ => { },
                reader => rows.Add([
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    Titled(reader.GetString(3))
                ]));
            table.Fill(rows);
        }
        catch (Exception ex)
        {
            table.Fail(ex);
        }
    }

    // ── Configurations ───────────────────────────────────────────────────────────────────────────────

    private Control BuildConfigurations()
    {
        // SSMS's "Configurations" page is database-*scoped* configurations — ALTER DATABASE SCOPED
        // CONFIGURATION, not sp_configure. sp_configure is server-level and belongs to Server Properties,
        // which this dialog is not.
        var table = new Table(["Name", "Value", "Value For Secondary"], [0, 160, 180]);
        _ = LoadConfigurationsAsync(table);
        return table.Control;
    }

    private async Task LoadConfigurationsAsync(Table table)
    {
        try
        {
            await using var connection = await OpenAsync();
            var rows = new List<string[]>();
            await RunAsync(connection,
                """
                SELECT name, ISNULL(CAST(value AS nvarchar(128)), ''),
                       ISNULL(CAST(value_for_secondary AS nvarchar(128)), '')
                FROM sys.database_scoped_configurations
                ORDER BY name
                """,
                _ => { },
                reader => rows.Add([reader.GetString(0), reader.GetString(1), reader.GetString(2)]));
            table.Fill(rows);
        }
        catch (Exception ex)
        {
            // The view arrived in SQL Server 2016 with the feature itself.
            table.Fail(ex);
        }
    }

    // ── Transaction Log Shipping ─────────────────────────────────────────────────────────────────────

    private Control BuildLogShipping()
    {
        var p = new PropPage();
        p.Row("Role", "role");
        p.Row("Backup Directory", "backupDirectory");
        p.Row("Backup Retention (minutes)", "retention");
        p.Row("Last Backup File", "lastBackup");
        p.Row("Monitor Server", "monitor");

        _ = LoadLogShippingAsync(p);

        return Scrolls(new StackPanel
        {
            Spacing = 6,
            Children =
            {
                p.Stack,
                Note("Status only. SSMS's page here is a configuration wizard — secondary servers, backup "
                    + "schedules, a monitor instance — and setting log shipping up is its own feature "
                    + "rather than a corner of this dialog.")
            }
        });
    }

    private async Task LoadLogShippingAsync(PropPage p)
    {
        try
        {
            await using var connection = await OpenAsync();
            var found = false;

            // msdb explicitly: the connection is pointed at the database this dialog is about, and the log
            // shipping tables live in msdb regardless.
            await RunAsync(connection,
                """
                SELECT 'Primary', pd.backup_directory, pd.backup_retention_period,
                       ISNULL(pd.last_backup_file, ''), ISNULL(pd.monitor_server, '')
                FROM msdb.dbo.log_shipping_primary_databases AS pd
                WHERE pd.primary_database = @db
                UNION ALL
                SELECT 'Secondary', '', 0, '', ISNULL(sd.secondary_database, '')
                FROM msdb.dbo.log_shipping_secondary_databases AS sd
                WHERE sd.secondary_database = @db
                """,
                cmd => cmd.Parameters.AddWithValue("@db", _database),
                reader =>
                {
                    found = true;
                    p.Set("role", reader.GetString(0));
                    p.Set("backupDirectory", Str(reader, 1));
                    p.Set("retention", reader.GetInt32(2).ToString());
                    p.Set("lastBackup", Str(reader, 3));
                    p.Set("monitor", Str(reader, 4));
                });

            if (!found)
            {
                p.Set("role", "Not configured");
                foreach (var key in new[] { "backupDirectory", "retention", "lastBackup", "monitor" })
                {
                    p.Set(key, "—");
                }
            }
        }
        catch (Exception ex)
        {
            // Reading msdb needs rights on msdb, which is separable from rights on this database.
            p.Fail(ex);
        }
    }

    // ── Extended Properties ──────────────────────────────────────────────────────────────────────────

    private readonly SelectTable _extendedPropertyTable = new(["Name", "Value"], [220, 0]);
    private readonly Dictionary<string, string> _originalExtendedProperties = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _extendedProperties = new(StringComparer.Ordinal);

    private Control BuildExtendedProperties()
    {
        var name = new TextBox { Width = 220, PlaceholderText = "Name" };
        var value = new TextBox { Width = 320, PlaceholderText = "Value" };

        var set = new Button { Content = "Add / update" };
        set.Click += (_, _) =>
        {
            if (name.Text is { Length: > 0 } key)
            {
                _extendedProperties[key] = value.Text ?? "";
                name.Text = value.Text = "";
                RefreshExtendedProperties();
            }
        };

        var remove = new Button { Content = "Remove" };
        remove.Click += (_, _) =>
        {
            var keys = _extendedProperties.Keys.ToList();
            if (_extendedPropertyTable.SelectedIndex is var i and >= 0 && i < keys.Count)
            {
                _extendedProperties.Remove(keys[i]);
                RefreshExtendedProperties();
            }
        };

        _extendedPropertyTable.SelectionChanged += () =>
        {
            var keys = _extendedProperties.Keys.ToList();
            if (_extendedPropertyTable.SelectedIndex is var i and >= 0 && i < keys.Count)
            {
                name.Text = keys[i];
                value.Text = _extendedProperties[keys[i]];
            }
        };

        _ = LoadExtendedPropertiesAsync();

        return Scrolls(new StackPanel
        {
            Spacing = 6,
            Children =
            {
                _extendedPropertyTable.Control,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Margin = new Thickness(0, 8, 0, 0),
                    Children = { name, value, set, remove }
                },
                Note("Database-level properties. \"None\" means this database genuinely has none — most do "
                    + "not. Changes are written when you press OK, with the rest of the dialog.")
            }
        });
    }

    private void RefreshExtendedProperties() => _extendedPropertyTable.Fill(
        [.. _extendedProperties.Select(p => new[] { new Cell(p.Key), new Cell(p.Value) })],
        "None.");

    private async Task LoadExtendedPropertiesAsync()
    {
        try
        {
            await using var connection = await OpenAsync();
            await RunAsync(connection,
                "SELECT name, CAST(value AS nvarchar(4000)) FROM sys.extended_properties WHERE class = 0 ORDER BY name",
                _ => { },
                reader =>
                {
                    var value = Str(reader, 1) ?? "";
                    _originalExtendedProperties[reader.GetString(0)] = value;
                    _extendedProperties[reader.GetString(0)] = value;
                });
            RefreshExtendedProperties();
        }
        catch (Exception ex)
        {
            _extendedPropertyTable.Fail(ex);
        }
    }

    // ── Query Store ──────────────────────────────────────────────────────────────────────────────────

    private Control BuildQueryStore()
    {
        var p = new PropPage();
        p.Section("General");
        p.Row("Operation Mode (Requested)", "requested");
        p.Row("Operation Mode (Actual)", "actual");
        p.Section("Monitoring");
        p.Row("Data Flush Interval (min)", "flush");
        p.Row("Statistics Collection Interval (min)", "interval");
        p.Section("Query Store Retention");
        p.Row("Max Size (MB)", "maxSize");
        p.Row("Current Storage Size (MB)", "currentSize");
        p.Row("Stale Query Threshold (Days)", "stale");
        p.Row("Size Based Cleanup Mode", "cleanup");
        p.Row("Query Store Capture Mode", "capture");

        // Its own PropPage and its own query: these four columns arrived in SQL Server 2019, and adding them
        // to the query above would put the whole page inside that version gate — it would report "Not
        // available on this server" on 2016/2017, where it works today.
        var policy = new PropPage();
        policy.Section("Query Store Capture Policy");
        policy.Row("Execution Count", "policyExecutions");
        policy.Row("Stale Threshold (hours)", "policyStale");
        policy.Row("Total Compile CPU Time (ms)", "policyCompileCpu");
        policy.Row("Total Execution CPU Time (ms)", "policyExecutionCpu");

        var usage = new ProgressBar { Minimum = 0, Maximum = 100, Height = 10, Margin = new Thickness(0, 6, 0, 0) };
        var usageText = FormBits.Label("");
        usageText.Margin = new Thickness(0, 4, 0, 0);

        _ = LoadQueryStoreAsync(p, policy, usage, usageText);

        return Scrolls(new StackPanel
        {
            Spacing = 4,
            Children =
            {
                p.Stack,
                policy.Stack,
                Header("Current Disk Usage"),
                usage,
                usageText
            }
        });
    }

    private async Task LoadQueryStoreAsync(PropPage p, PropPage policy, ProgressBar usage, TextBlock usageText)
    {
        try
        {
            await using var connection = await OpenAsync();
            var found = false;
            await LoadCapturePolicyAsync(connection, policy);
            await RunAsync(connection,
                """
                SELECT desired_state_desc, actual_state_desc, flush_interval_seconds, interval_length_minutes,
                       max_storage_size_mb, current_storage_size_mb, stale_query_threshold_days,
                       size_based_cleanup_mode_desc, query_capture_mode_desc
                FROM sys.database_query_store_options
                """,
                _ => { },
                reader =>
                {
                    found = true;
                    p.Set("requested", Titled(Str(reader, 0)));
                    p.Set("actual", Titled(Str(reader, 1)));
                    p.Set("flush", (reader.GetInt64(2) / 60).ToString());
                    p.Set("interval", reader.GetInt64(3).ToString());
                    p.Set("maxSize", reader.GetInt64(4).ToString("N0"));
                    p.Set("currentSize", reader.GetInt64(5).ToString("N0"));
                    SetDiskUsage(usage, usageText, reader.GetInt64(5), reader.GetInt64(4));
                    p.Set("stale", reader.GetInt64(6).ToString());
                    p.Set("cleanup", Titled(Str(reader, 7)));
                    p.Set("capture", Titled(Str(reader, 8)));
                });

            if (!found)
            {
                p.Set("requested", "Off");
                foreach (var k in new[] { "actual", "flush", "interval", "maxSize", "currentSize", "stale", "cleanup", "capture" })
                {
                    p.Set(k, "—");
                }
            }
        }
        catch (Exception ex)
        {
            // Query Store view is absent before SQL Server 2016.
            p.Set("requested", "Not available on this server");
            foreach (var k in new[] { "actual", "flush", "interval", "maxSize", "currentSize", "stale", "cleanup", "capture" })
            {
                p.Set(k, "—");
            }
            _ = ex;
        }
    }

    // SSMS draws a donut here. There is no charting library in this repo and no custom-drawn control
    // anywhere in it, and one pie chart is not a reason to add a dependency to a plugin that references
    // Avalonia core by design — a bar and the numbers carry the same information.
    // ponytail: bar over a chart; revisit only if more than one page here wants to draw.
    private static void SetDiskUsage(ProgressBar bar, TextBlock text, long currentMb, long maxMb) =>
        Dispatcher.UIThread.Post(() =>
        {
            var percent = maxMb > 0 ? Math.Clamp(currentMb * 100d / maxMb, 0, 100) : 0;
            bar.Value = percent;
            text.Text = maxMb > 0
                ? $"{currentMb:N0} MB of {maxMb:N0} MB ({percent:N1}%)"
                : $"{currentMb:N0} MB used; no maximum set.";
        });

    /// <summary>
    /// The two FILESTREAM rows. They are not columns of <c>sys.databases</c> — they live in
    /// <c>sys.database_filestream_options</c>, one row per database — and asking sys.databases for them
    /// failed the entire Options page, not just those two rows, because the whole load shares one try.
    /// </summary>
    /// <remarks>
    /// Its own query with its own try for the same reason the Query Store capture policy has one: a page
    /// that reads twenty settings should not lose all of them because the twenty-first is unavailable.
    /// </remarks>
    private static async Task LoadFilestreamOptionsAsync(SqlConnection connection, PropPage p)
    {
        try
        {
            var found = false;
            await RunAsync(connection,
                """
                SELECT directory_name, non_transacted_access_desc
                FROM sys.database_filestream_options
                WHERE database_id = DB_ID()
                """,
                _ => { },
                reader =>
                {
                    found = true;
                    p.Set("filestreamDirectory", Str(reader, 0));
                    p.Set("filestreamAccess", Titled(Str(reader, 1)));
                });

            if (!found)
            {
                p.Set("filestreamDirectory", "—");
                p.Set("filestreamAccess", "—");
            }
        }
        catch
        {
            // The view arrived in SQL Server 2012 with FILESTREAM itself.
            p.Set("filestreamDirectory", "—");
            p.Set("filestreamAccess", "Not available on this server");
        }
    }

    private async Task LoadCapturePolicyAsync(SqlConnection connection, PropPage policy)
    {
        var keys = new[] { "policyExecutions", "policyStale", "policyCompileCpu", "policyExecutionCpu" };
        try
        {
            await RunAsync(connection,
                """
                SELECT capture_policy_execution_count, capture_policy_stale_threshold_hours,
                       capture_policy_total_compile_cpu_time_ms, capture_policy_total_execution_cpu_time_ms
                FROM sys.database_query_store_options
                """,
                _ => { },
                reader =>
                {
                    policy.Set("policyExecutions", reader.GetInt32(0).ToString("N0"));
                    policy.Set("policyStale", reader.GetInt32(1).ToString("N0"));
                    policy.Set("policyCompileCpu", reader.GetInt64(2).ToString("N0"));
                    policy.Set("policyExecutionCpu", reader.GetInt64(3).ToString("N0"));
                });
        }
        catch
        {
            // The four columns are SQL Server 2019+. Kept in its own try so a 2016/2017 server loses this
            // section and nothing else — folded into the main query it would take the whole page down.
            foreach (var key in keys)
            {
                policy.Set(key, "Requires SQL Server 2019");
            }
        }
    }

    // ── Data access helpers ──────────────────────────────────────────────────────────────────────────

    private async Task<SqlConnection> OpenAsync()
    {
        // Repoint at the target database so FILEPROPERTY/sys.database_files/DB_ID resolve to it.
        var connectionString = new SqlConnectionStringBuilder(_context.Profile.ConnectionString) { InitialCatalog = _database }.ConnectionString;
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task RunAsync(SqlConnection connection, string sql, Action<SqlCommand> configure, Action<SqlDataReader> read)
    {
        await using var command = new SqlCommand(sql, connection);
        configure(command);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            read(reader);
        }
    }

    private static async Task TryAsync(Func<Task> action, Action onFail)
    {
        try { await action(); }
        catch { onFail(); }
    }

    private static string? Str(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    // ── Formatting helpers ───────────────────────────────────────────────────────────────────────────

    // Feeds both read-only rows and the Options editors, whose writers match on this exact wording — so it
    // stays "True"/"False" and grid cells get Tick() instead.
    private static string YesNo(bool value) => value ? "True" : "False";

    /// <summary>
    /// A boolean in a grid cell. A tick scans faster than the word "True" in a column of them.
    /// </summary>
    /// <remarks>
    /// Not a ToggleSwitch, which is what was asked for here: these are read-only facts about a filegroup,
    /// not settings, and a switch that does not move reads as a broken control rather than as a value.
    /// Making them genuinely editable — <c>ALTER DATABASE … MODIFY FILEGROUP READ_ONLY / DEFAULT</c> —
    /// would earn real toggles, and is worth its own ticket rather than being smuggled into a polish pass.
    /// </remarks>
    private static string Tick(bool value) => value ? "✓" : "—";

    // "SIMPLE" -> "Simple", "READ_ONLY" -> "Read Only" — SSMS shows these desc columns title-cased.
    private static string Titled(string? desc)
    {
        if (string.IsNullOrEmpty(desc))
        {
            return "—";
        }

        var words = desc.Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i].ToLowerInvariant();
            words[i] = char.ToUpperInvariant(w[0]) + w[1..];
        }
        return string.Join(' ', words);
    }

    private static string FileType(byte type) => type switch
    {
        0 => "ROWS Data",
        1 => "LOG",
        2 => "FILESTREAM",
        4 => "Full-text",
        _ => "Other"
    };

    // Combine growth + max size into SSMS' single "Autogrowth / Maxsize" cell, e.g. "By 64 MB, Unlimited".
    private static string Autogrowth(bool isPercent, int growth, int maxSize)
    {
        var growthText = growth == 0
            ? "None"
            : isPercent ? $"By {growth} percent" : $"By {growth * 8 / 1024} MB";
        var maxText = maxSize switch
        {
            -1 => "Unlimited",
            0 => "Restricted",
            _ => $"Limited to {(long)maxSize * 8 / 1024:N0} MB"
        };
        return $"{growthText}, {maxText}";
    }

    private static string CompatLevel(byte level)
    {
        var product = level switch
        {
            160 => "SQL Server 2022",
            150 => "SQL Server 2019",
            140 => "SQL Server 2017",
            130 => "SQL Server 2016",
            120 => "SQL Server 2014",
            110 => "SQL Server 2012",
            100 => "SQL Server 2008",
            _ => "SQL Server"
        };
        return $"{product} ({level})";
    }

    // Split a stored physical path into (directory, file name), handling both Windows (\) and POSIX (/)
    // separators regardless of the client OS (Path.GetFileName only splits the host platform's separator).
    private static (string Dir, string File) SplitPath(string path)
    {
        var idx = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
        return idx < 0 ? ("", path) : (path[..idx], path[(idx + 1)..]);
    }
}
