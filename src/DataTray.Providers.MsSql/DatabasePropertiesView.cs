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
    [
        "General", "Files", "Filegroups", "Options", "Change Tracking", "Permissions", "Extended Properties",
        "Query Store", "Configurations", "Transaction Log Shipping"
    ];

    private readonly NodeInfoContext _context;
    private readonly string _database;
    // A ScrollViewer rather than a ContentControl: the dialog no longer scrolls the whole view,
    // so each page carries its own scrolling and the page rail stays where it is.
    private readonly ScrollViewer _host = new();
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
                8 => BuildConfigurations(),
                9 => BuildLogShipping(),
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
        p.Row("Transaction Log Shipping", "logShipping");

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
        // SSMS shows three grids here. The old single one filtered on type = 'FG', so a database with
        // FILESTREAM or memory-optimized data showed nothing about it at all — not an empty section, no
        // section.
        var rows = new Table(["Name", "Files", "Read-Only", "Default", "Autogrow All Files"], [220, 80, 90, 90, 130]);
        var filestream = new Table(["Name", "Files", "Read-Only", "Default"], [220, 80, 90, 90]);
        var memoryOptimized = new Table(["Name", "Files"], [220, 80]);

        _ = LoadFilegroupsAsync(rows, filestream, memoryOptimized);

        return new StackPanel
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
        };
    }

    private static TextBlock Header(string text) => new()
    {
        Text = text,
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(0, 12, 0, 4)
    };

    private async Task LoadFilegroupsAsync(Table rows, Table filestream, Table memoryOptimized)
    {
        try
        {
            await using var connection = await OpenAsync();
            List<string[]> row = [], fs = [], mo = [];

            // One pass over every data space type rather than three queries: they differ only in which
            // columns mean anything, and is_autogrow_all_files does not exist on a FILESTREAM filegroup.
            await RunAsync(connection,
                """
                SELECT fg.name, fg.type, COUNT(df.file_id), fg.is_read_only, fg.is_default,
                       fg.is_autogrow_all_files
                FROM sys.filegroups fg
                LEFT JOIN sys.database_files df ON df.data_space_id = fg.data_space_id
                GROUP BY fg.name, fg.type, fg.is_read_only, fg.is_default, fg.is_autogrow_all_files
                ORDER BY fg.name
                """,
                _ => { },
                reader =>
                {
                    var name = reader.GetString(0);
                    var files = reader.GetInt32(2).ToString();
                    switch (reader.GetString(1).Trim())
                    {
                        case "FG":
                            row.Add([name, files, YesNo(reader.GetBoolean(3)), YesNo(reader.GetBoolean(4)),
                                YesNo(reader.GetBoolean(5))]);
                            break;
                        case "FD":
                            fs.Add([name, files, YesNo(reader.GetBoolean(3)), YesNo(reader.GetBoolean(4))]);
                            break;
                        case "FX":
                            mo.Add([name, files]);
                            break;
                    }
                });

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
        p.Section("Cursor");
        p.Row("Close Cursor on Commit Enabled", "cursorClose");
        p.Row("Default Cursor", "cursorDefault");
        p.Section("Recovery");
        p.Row("Page Verify", "pageVerify");
        p.Row("Target Recovery Time (Seconds)", "targetRecovery");
        p.Section("Miscellaneous");
        p.Row("ANSI NULL Default", "ansiNullDefault");
        p.Row("ANSI NULLS Enabled", "ansiNulls");
        p.Row("ANSI Padding Enabled", "ansiPadding");
        p.Row("ANSI Warnings Enabled", "ansiWarnings");
        p.Row("Arithmetic Abort Enabled", "arithAbort");
        p.Row("Concatenate Null Yields Null", "concatNull");
        p.Row("Numeric Round-Abort", "numericRoundAbort");
        p.Row("Quoted Identifiers Enabled", "quotedIdentifier");
        p.Row("Recursive Triggers Enabled", "recursiveTriggers");
        p.Row("Trustworthy", "trustworthy");
        p.Row("Date Correlation Optimization Enabled", "dateCorrelation");
        p.Row("Parameterization", "parameterization");
        p.Row("Delayed Durability", "delayedDurability");
        p.Section("Service Broker");
        p.Row("Broker Enabled", "broker");
        p.Row("Honor Broker Priority", "brokerPriority");
        p.Row("Service Broker Identifier", "brokerGuid");
        p.Section("FILESTREAM");
        p.Row("FILESTREAM Directory Name", "filestreamDirectory");
        p.Row("FILESTREAM Non-Transacted Access", "filestreamAccess");
        p.Section("State");
        p.Row("Database State", "state");
        p.Row("Database Read-Only", "readOnly");
        p.Row("Restrict Access", "userAccess");
        p.Row("Encryption Enabled", "encrypted");
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
                       is_broker_enabled, snapshot_isolation_state_desc, is_read_committed_snapshot_on,
                       is_cursor_close_on_commit_on, is_local_cursor_default, target_recovery_time_in_seconds,
                       is_ansi_null_default_on, is_ansi_nulls_on, is_ansi_padding_on, is_ansi_warnings_on,
                       is_arithabort_on, is_concat_null_yields_null_on, is_numeric_roundabort_on,
                       is_quoted_identifier_on, is_recursive_triggers_on, is_trustworthy_on,
                       is_date_correlation_on, is_parameterization_forced, delayed_durability_desc,
                       is_honor_broker_priority_on, service_broker_guid, state_desc,
                       is_filestream_non_transacted_access_desc, filestream_directory_name
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
                    p.Set("filestreamAccess", Titled(Str(reader, 36)));
                    p.Set("filestreamDirectory", Str(reader, 37));
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
        var table = new Table(["Grantee", "Grantor", "Permission", "State"], [180, 180, 210, 100]);
        _ = LoadPermissionsAsync(table);

        return new StackPanel
        {
            Spacing = 6,
            Children =
            {
                table.Control,
                new TextBlock
                {
                    Text = "Permissions on the database itself, which is what this dialog is about. Grants on "
                        + "tables, views and other objects live with those objects.",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.65,
                    Margin = new Thickness(0, 8, 0, 0)
                }
            }
        };
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
        var table = new Table(["Name", "Value", "Value For Secondary"], [320, 160, 180]);
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

        return new StackPanel
        {
            Spacing = 6,
            Children =
            {
                p.Stack,
                new TextBlock
                {
                    Text = "Status only. SSMS's page here is a configuration wizard — secondary servers, "
                        + "backup schedules, a monitor instance — and setting log shipping up is its own "
                        + "feature rather than a corner of this dialog.",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.65,
                    Margin = new Thickness(0, 12, 0, 0)
                }
            }
        };
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
        var usageText = new TextBlock { Opacity = 0.8, Margin = new Thickness(0, 4, 0, 0) };

        _ = LoadQueryStoreAsync(p, policy, usage, usageText);

        return new StackPanel
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
        };
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
