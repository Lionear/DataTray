using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
        ["General", "Files", "Filegroups", "Options", "Change Tracking", "Permissions", "Extended Properties", "Query Store"];

    private readonly NodeInfoContext _context;
    private readonly string _database;
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

        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(rail, 0);
        Grid.SetColumn(_host, 1);
        layout.Children.Add(rail);
        layout.Children.Add(_host);
        Content = layout;

        ShowPage(0);
    }

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
                _ => new StackPanel()
            };
            // No ScrollViewer here — the host dialog already wraps this whole view in one; nesting a second
            // would leave the inner content unbounded and never scroll.
            _built[index] = new StackPanel { Margin = new Thickness(16, 0, 8, 8), Spacing = 4, Children = { page } };
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

        p.Values["name"].Text = _database;
        _ = LoadGeneralAsync(p);
        return p.Stack;
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

            if (p.Values["lastBackup"].Text is "…") p.Set("lastBackup", "None");
            if (p.Values["lastLogBackup"].Text is "…") p.Set("lastLogBackup", "None");
        }
        catch (Exception ex)
        {
            p.Fail(ex);
        }
    }

    // ── Files ────────────────────────────────────────────────────────────────────────────────────────

    private Control BuildFiles()
    {
        var table = new Table(["Logical Name", "File Type", "Filegroup", "Size (MB)", "Autogrowth / Maxsize", "Path", "File Name"],
            [140, 90, 90, 75, 170, 180, 150]);
        _ = LoadFilesAsync(table);
        return table.Control;
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
                        dir,
                        file
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
        var table = new Table(["Name", "Files", "Read-Only", "Default"], [240, 90, 100, 100]);
        _ = LoadFilegroupsAsync(table);
        return table.Control;
    }

    private async Task LoadFilegroupsAsync(Table table)
    {
        try
        {
            await using var connection = await OpenAsync();
            var rows = new List<string[]>();
            await RunAsync(connection,
                """
                SELECT fg.name, COUNT(df.file_id), fg.is_read_only, fg.is_default
                FROM sys.filegroups fg
                LEFT JOIN sys.database_files df ON df.data_space_id = fg.data_space_id
                WHERE fg.type = 'FG'
                GROUP BY fg.name, fg.is_read_only, fg.is_default
                ORDER BY fg.name
                """,
                _ => { },
                reader => rows.Add([
                    reader.GetString(0),
                    reader.GetInt32(1).ToString(),
                    YesNo(reader.GetBoolean(2)),
                    YesNo(reader.GetBoolean(3))
                ]));
            table.Fill(rows);
        }
        catch (Exception ex)
        {
            table.Fail(ex);
        }
    }

    // ── Options ──────────────────────────────────────────────────────────────────────────────────────

    private Control BuildOptions()
    {
        var p = new PropPage();
        p.Section("General");
        p.Row("Collation", "collation");
        p.Row("Recovery Model", "recovery");
        p.Row("Compatibility Level", "compat");
        p.Row("Containment Type", "containment");
        p.Section("Automatic");
        p.Row("Auto Close", "autoClose");
        p.Row("Auto Create Statistics", "autoCreateStats");
        p.Row("Auto Create Incremental Statistics", "autoCreateStatsInc");
        p.Row("Auto Shrink", "autoShrink");
        p.Row("Auto Update Statistics", "autoUpdateStats");
        p.Row("Auto Update Statistics Asynchronously", "autoUpdateStatsAsync");
        p.Section("Recovery / Cursor");
        p.Row("Page Verify", "pageVerify");
        p.Section("State");
        p.Row("Database Read-Only", "readOnly");
        p.Row("Restrict Access", "userAccess");
        p.Row("Encryption Enabled", "encrypted");
        p.Row("Broker Enabled", "broker");
        p.Row("Allow Snapshot Isolation", "snapshotIso");
        p.Row("Is Read Committed Snapshot On", "rcsi");

        _ = LoadOptionsAsync(p);
        return p.Stack;
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
                       is_broker_enabled, snapshot_isolation_state_desc, is_read_committed_snapshot_on
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
                    p.Set("pageVerify", Str(reader, 10));
                    p.Set("readOnly", YesNo(reader.GetBoolean(11)));
                    p.Set("userAccess", Titled(Str(reader, 12)));
                    p.Set("encrypted", YesNo(reader.GetBoolean(13)));
                    p.Set("broker", YesNo(reader.GetBoolean(14)));
                    p.Set("snapshotIso", Titled(Str(reader, 15)));
                    p.Set("rcsi", YesNo(reader.GetBoolean(16)));
                });
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
        return p.Stack;
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
        var table = new Table(["Grantee", "Permission", "Securable Type", "State"], [180, 210, 150, 100]);
        _ = LoadPermissionsAsync(table);
        return table.Control;
    }

    private async Task LoadPermissionsAsync(Table table)
    {
        try
        {
            await using var connection = await OpenAsync();
            var rows = new List<string[]>();
            await RunAsync(connection,
                """
                SELECT grantee.name, dp.permission_name, dp.class_desc, dp.state_desc
                FROM sys.database_permissions dp
                JOIN sys.database_principals grantee ON dp.grantee_principal_id = grantee.principal_id
                ORDER BY grantee.name, dp.permission_name
                """,
                _ => { },
                reader => rows.Add([
                    reader.GetString(0),
                    reader.GetString(1),
                    Titled(reader.GetString(2)),
                    Titled(reader.GetString(3))
                ]));
            table.Fill(rows);
        }
        catch (Exception ex)
        {
            table.Fail(ex);
        }
    }

    // ── Extended Properties ──────────────────────────────────────────────────────────────────────────

    private Control BuildExtendedProperties()
    {
        var table = new Table(["Name", "Value"], [200, 360]);
        _ = LoadExtendedPropertiesAsync(table);
        return table.Control;
    }

    private async Task LoadExtendedPropertiesAsync(Table table)
    {
        try
        {
            await using var connection = await OpenAsync();
            var rows = new List<string[]>();
            await RunAsync(connection,
                "SELECT name, CAST(value AS nvarchar(4000)) FROM sys.extended_properties WHERE class = 0 ORDER BY name",
                _ => { },
                reader => rows.Add([reader.GetString(0), Str(reader, 1) ?? ""]));
            table.Fill(rows);
        }
        catch (Exception ex)
        {
            table.Fail(ex);
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

        _ = LoadQueryStoreAsync(p);
        return p.Stack;
    }

    private async Task LoadQueryStoreAsync(PropPage p)
    {
        try
        {
            await using var connection = await OpenAsync();
            var found = false;
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

    private static string YesNo(bool value) => value ? "True" : "False";

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
