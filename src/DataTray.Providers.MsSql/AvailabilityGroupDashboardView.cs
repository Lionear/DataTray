using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DataTray.Sdk.Ui;
using Microsoft.Data.SqlClient;

namespace DataTray.Providers.MsSql;

/// <summary>
/// SE-284's Always On dashboard (SE-247 §2) — the read-only "who is primary, is anything behind, and how
/// far" view for one availability group, opened the same way Database/Job Properties are
/// (<see cref="ICustomNodeInfoUi"/>). Read + refresh only: every write (Fail Over…, New Availability
/// Group…) is its own SE-247 tool, so this view has no footer of its own and leaves the host's Close bar
/// in place.
/// </summary>
/// <remarks>
/// The only node-info view in DataTray that polls. Every other one reads once, because schema does not
/// move while you look at it — queue depth does, and a dashboard whose numbers are from whenever you
/// opened it is worse than no dashboard: it has the shape of a live one and it lies. Polls every
/// <see cref="PollInterval"/> while the dialog is open, and stops on <see cref="Control.Unloaded"/> —
/// <c>NodeInfoDialog</c> never disposes the view it hosts, so nothing else would stop the timer once the
/// window closes, and this is the first node-info view to own a resource that needs that.
/// </remarks>
public sealed class AvailabilityGroupDashboardView : UserControl
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly NodeInfoContext _context;
    private readonly string _group;

    private readonly TextBlock _age = new() { Opacity = 0.7, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _bannerText = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _banner;

    private readonly PropPage _groupInfo = new();

    private readonly Table _replicas = new(
        ["Replica", "Role", "Availability mode", "Failover mode", "Connected", "Sync health", "Readable", "Seeding", "Backup priority"],
        [150, 80, 150, 105, 100, 120, 130, 90, 0]);

    private readonly Table _databases = new(
        ["Database", "Replica", "State", "Suspended", "Log send queue", "Redo queue", "Last commit", "Behind by"],
        [140, 150, 140, 170, 110, 100, 90, 0]);

    private DispatcherTimer? _timer;

    public AvailabilityGroupDashboardView(NodeInfoContext context)
    {
        _context = context;
        _group = context.Node.Name;

        _bannerText.Themed(TextBlock.ForegroundProperty, "SETextPrimaryBrush");
        var refresh = new Button { Content = "Refresh", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        refresh.Click += (_, _) => _ = LoadAsync();

        var bannerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_bannerText, 0);
        Grid.SetColumn(_age, 1);
        Grid.SetColumn(refresh, 2);
        bannerRow.Children.Add(_bannerText);
        bannerRow.Children.Add(_age);
        bannerRow.Children.Add(refresh);

        _banner = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 9),
            Child = bannerRow
        };
        _banner.Themed(Border.BackgroundProperty, "SESecondaryBgBrush");
        _banner.Themed(Border.BorderBrushProperty, "SEHairlineBrush");

        _groupInfo.Section("Group");
        _groupInfo.Row("Cluster type", "clusterType");
        _groupInfo.Row("Listener", "listener");
        _groupInfo.Row("Backup preference", "backupPreference");
        _groupInfo.Row("Required synchronized secondaries", "requiredSync");
        _groupInfo.Row("Database-level health detection", "healthDetection");

        var behindByHint = new TextBlock
        {
            Text = "\"Behind by\" is the primary's last commit time minus this replica's, in seconds. It is the " +
                   "number a person can act on; the log send/redo queue columns are the DMVs' own kilobytes — " +
                   "bytes explain why a replica is behind, seconds say how bad it is.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11.5,
            Opacity = 0.75
        };

        var stack = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                _banner,
                _groupInfo.Stack,
                FormBits.Section("Replicas"),
                _replicas.Control,
                FormBits.Section("Databases"),
                _databases.Control,
                behindByHint
            }
        };

        // No outer ScrollViewer wrapper needed at the host level (NodeInfoDialog does not add one for
        // provider views), but this page can run tall with several replicas/databases, so it scrolls itself
        // the same way every other page in this provider does.
        Content = new ScrollViewer
        {
            Padding = new Thickness(0, 0, 4, 4),
            Content = stack
        };

        Unloaded += (_, _) => StopPolling();

        _timer = new DispatcherTimer { Interval = PollInterval };
        _timer.Tick += (_, _) => _ = LoadAsync();
        _timer.Start();
        _ = LoadAsync();
    }

    private void StopPolling()
    {
        if (_timer is null)
        {
            return;
        }

        _timer.Stop();
        _timer = null;
    }

    private sealed record ReplicaRow(
        string Name, string Role, string AvailabilityMode, string FailoverMode,
        string Connected, string SyncHealth, string Readable, string Seeding, int BackupPriority);

    private sealed record DatabaseRow(
        string Database, string Replica, bool IsPrimaryRow, string State,
        bool IsSuspended, string? SuspendReason, long? LogSendQueueKb, long? RedoQueueKb, DateTime? LastCommit);

    private async Task LoadAsync()
    {
        try
        {
            await using var connection = new SqlConnection(_context.Profile.ConnectionString);
            await connection.OpenAsync();

            Guid groupId;
            string? clusterTypeDesc, backupPreferenceDesc, primaryReplica, groupSyncHealthDesc;
            int requiredSync, failureConditionLevel, healthCheckTimeoutMs;
            bool dbFailover;

            await using (var command = new SqlCommand(
                """
                SELECT ag.group_id, ag.cluster_type_desc, ag.automated_backup_preference_desc,
                       ag.required_synchronized_secondaries_to_commit, ag.db_failover,
                       ag.failure_condition_level, ag.health_check_timeout,
                       ags.primary_replica, ags.synchronization_health_desc
                FROM sys.availability_groups ag
                LEFT JOIN sys.dm_hadr_availability_group_states ags ON ags.group_id = ag.group_id
                WHERE ag.name = @name
                """, connection))
            {
                command.Parameters.AddWithValue("@name", _group);
                await using var reader = await command.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    Dispatcher.UIThread.Post(() => Fail("This availability group no longer exists on this instance."));
                    return;
                }

                groupId = reader.GetGuid(0);
                clusterTypeDesc = reader.IsDBNull(1) ? null : reader.GetString(1);
                backupPreferenceDesc = reader.IsDBNull(2) ? null : reader.GetString(2);
                requiredSync = ToInt32(reader, 3) ?? 0;
                dbFailover = !reader.IsDBNull(4) && reader.GetBoolean(4);
                failureConditionLevel = ToInt32(reader, 5) ?? 0;
                healthCheckTimeoutMs = ToInt32(reader, 6) ?? 0;
                primaryReplica = reader.IsDBNull(7) ? null : reader.GetString(7);
                groupSyncHealthDesc = reader.IsDBNull(8) ? null : reader.GetString(8);
            }

            var clusterText = await ClusterTextAsync(connection, clusterTypeDesc);
            var listenerText = await ListenerTextAsync(connection, groupId);
            var replicaRows = await ReplicaRowsAsync(connection, groupId);
            var databaseRows = await DatabaseRowsAsync(connection, groupId);

            var bannerText = AvailabilityGroupStatus.Summary(primaryReplica, groupSyncHealthDesc);

            Dispatcher.UIThread.Post(() =>
            {
                _bannerText.Text = bannerText;
                if (HealthBrush(groupSyncHealthDesc) is { } tint)
                {
                    _bannerText.Foreground = tint;
                }
                else
                {
                    _bannerText.Themed(TextBlock.ForegroundProperty, "SETextPrimaryBrush");
                }

                _age.Text = $"refreshed {DateTime.Now:T}";

                _groupInfo.Set("clusterType", clusterText);
                _groupInfo.Set("listener", listenerText);
                _groupInfo.Set("backupPreference", AvailabilityGroupStatus.BackupPreferenceText(backupPreferenceDesc));
                _groupInfo.Set("requiredSync", requiredSync.ToString());
                _groupInfo.Set("healthDetection",
                    $"DB_FAILOVER = {(dbFailover ? "ON" : "OFF")} · failure condition level {failureConditionLevel} · " +
                    $"health check timeout {healthCheckTimeoutMs} ms");
            });

            _replicas.Fill(replicaRows.Select(r => new[]
            {
                r.Name, r.Role, r.AvailabilityMode, r.FailoverMode, r.Connected, r.SyncHealth, r.Readable,
                r.Seeding, r.BackupPriority.ToString()
            }).ToList());

            _databases.Fill(databaseRows.Select(d =>
            {
                var primaryCommit = databaseRows
                    .FirstOrDefault(x => x.Database == d.Database && x.IsPrimaryRow)?.LastCommit;
                var behindBy = AvailabilityGroupStatus.BehindBy(d.IsPrimaryRow, primaryCommit, d.LastCommit);

                return new[]
                {
                    d.Database, d.Replica, d.State, AvailabilityGroupStatus.Suspended(d.IsSuspended, d.SuspendReason),
                    QueueText(d.LogSendQueueKb), QueueText(d.RedoQueueKb),
                    d.LastCommit?.ToString("T") ?? "—", behindBy ?? "—"
                };
            }).ToList());
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => Fail(ex.Message));
            _groupInfo.Fail(ex);
            _replicas.Fail(ex);
            _databases.Fail(ex);
        }
    }

    private static async Task<string> ClusterTextAsync(SqlConnection connection, string? clusterTypeDesc)
    {
        switch (clusterTypeDesc)
        {
            case "NONE":
                return "NONE — read-scale, no cluster, no automatic failover";
            case "EXTERNAL":
                return "EXTERNAL — Pacemaker-managed (Linux)";
            case "WSFC":
                try
                {
                    await using var command = new SqlCommand(
                        """
                        SELECT c.cluster_name, c.quorum_state_desc,
                               (SELECT COUNT(*) FROM sys.dm_hadr_cluster_members WHERE member_type_desc = 'CLUSTER_NODE')
                        FROM sys.dm_hadr_cluster c
                        """, connection);
                    await using var reader = await command.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        var quorum = reader.IsDBNull(1) ? "unknown" : reader.GetString(1).Replace('_', ' ').ToLowerInvariant();
                        return $"WSFC — cluster {reader.GetString(0)}, {ToInt32(reader, 2) ?? 0} nodes, quorum {quorum}";
                    }
                }
                catch (SqlException)
                {
                    // Needs VIEW SERVER STATE; not worth failing the whole dashboard load over one field.
                }

                return "WSFC (cluster details unavailable — check VIEW SERVER STATE on this connection)";
            default:
                return clusterTypeDesc ?? "Unknown";
        }
    }

    private static async Task<string> ListenerTextAsync(SqlConnection connection, Guid groupId)
    {
        await using var command = new SqlCommand(
            """
            SELECT l.dns_name, l.port, ip.ip_address, ip.state_desc
            FROM sys.availability_group_listeners l
            LEFT JOIN sys.availability_group_listener_ip_addresses ip ON ip.listener_id = l.listener_id
            WHERE l.group_id = @groupId
            """, connection);
        command.Parameters.AddWithValue("@groupId", groupId);

        string? dnsName = null;
        int port = 0;
        var addresses = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            dnsName ??= reader.IsDBNull(0) ? null : reader.GetString(0);
            if (port == 0 && !reader.IsDBNull(1))
            {
                port = ToInt32(reader, 1) ?? 0;
            }

            if (!reader.IsDBNull(2))
            {
                var state = reader.IsDBNull(3) ? "unknown" : reader.GetString(3).ToLowerInvariant();
                addresses.Add($"{reader.GetString(2)} ({state})");
            }
        }

        return dnsName is null
            ? "No listener configured"
            : $"{dnsName} : {port}" + (addresses.Count > 0 ? " — " + string.Join(", ", addresses) : "");
    }

    private static async Task<List<ReplicaRow>> ReplicaRowsAsync(SqlConnection connection, Guid groupId)
    {
        await using var command = new SqlCommand(
            """
            SELECT ar.replica_server_name, ars.role_desc, ar.availability_mode_desc, ar.failover_mode_desc,
                   ars.connected_state_desc, ars.synchronization_health_desc,
                   ar.secondary_role_allow_connections_desc, ar.seeding_mode_desc, ar.backup_priority
            FROM sys.availability_replicas ar
            JOIN sys.dm_hadr_availability_replica_states ars ON ars.replica_id = ar.replica_id
            WHERE ar.group_id = @groupId
            ORDER BY CASE ars.role_desc WHEN 'PRIMARY' THEN 0 ELSE 1 END, ar.replica_server_name
            """, connection);
        command.Parameters.AddWithValue("@groupId", groupId);

        var rows = new List<ReplicaRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var role = reader.GetString(1);
            rows.Add(new ReplicaRow(
                reader.GetString(0), role.ToLowerInvariant(),
                reader.IsDBNull(2) ? "—" : reader.GetString(2),
                reader.IsDBNull(3) ? "—" : reader.GetString(3),
                reader.IsDBNull(4) ? "—" : reader.GetString(4).ToLowerInvariant(),
                reader.IsDBNull(5) ? "—" : reader.GetString(5).Replace('_', ' ').ToLowerInvariant(),
                AvailabilityGroupStatus.Readable(role, reader.IsDBNull(6) ? null : reader.GetString(6)),
                reader.IsDBNull(7) ? "—" : reader.GetString(7),
                ToInt32(reader, 8) ?? 0));
        }

        return rows;
    }

    private static async Task<List<DatabaseRow>> DatabaseRowsAsync(SqlConnection connection, Guid groupId)
    {
        await using var command = new SqlCommand(
            """
            SELECT dcs.database_name, ar.replica_server_name, ars.role_desc, drs.synchronization_state_desc,
                   drs.is_suspended, drs.suspend_reason_desc, drs.log_send_queue_size, drs.redo_queue_size,
                   drs.last_commit_time
            FROM sys.dm_hadr_database_replica_states drs
            JOIN sys.dm_hadr_database_replica_cluster_states dcs
                ON dcs.replica_id = drs.replica_id AND dcs.group_database_id = drs.group_database_id
            JOIN sys.availability_replicas ar ON ar.replica_id = drs.replica_id
            JOIN sys.dm_hadr_availability_replica_states ars ON ars.replica_id = drs.replica_id
            WHERE ar.group_id = @groupId
            ORDER BY dcs.database_name, CASE ars.role_desc WHEN 'PRIMARY' THEN 0 ELSE 1 END, ar.replica_server_name
            """, connection);
        command.Parameters.AddWithValue("@groupId", groupId);

        var rows = new List<DatabaseRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var isPrimary = reader.GetString(2) == "PRIMARY";
            rows.Add(new DatabaseRow(
                reader.GetString(0), reader.GetString(1), isPrimary,
                reader.IsDBNull(3) ? "—" : reader.GetString(3).ToLowerInvariant(),
                !reader.IsDBNull(4) && reader.GetBoolean(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                ToInt64(reader, 6),
                ToInt64(reader, 7),
                reader.IsDBNull(8) ? null : reader.GetDateTime(8)));
        }

        return rows;
    }

    // sys.availability_replicas.backup_priority turned out to be int on a live SQL Server 2022 instance,
    // not the tinyint its 0-100 range suggested (Rick's first live test, k9-prod: "Unable to cast object
    // of type 'System.Int32' to type 'System.Byte'") — Microsoft Learn's own column-type tables are not
    // reliable enough to type-pin a SqlDataReader read against, so every "small" numeric DMV column here
    // goes through Convert.ToInt32/Int64 on the boxed value instead of a type-specific Get*, the same
    // defensive shape MsSqlProvider.Nullable already uses elsewhere in this provider.
    private static int? ToInt32(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal));

    private static long? ToInt64(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToInt64(reader.GetValue(ordinal));

    // log_send_queue_size/redo_queue_size are kilobytes per sys.dm_hadr_database_replica_states — a raw
    // "1789" next to a column called "queue" reads as a count, not a size, so this always shows the unit.
    private static string QueueText(long? kb) => kb switch
    {
        null => "—",
        0 => "0 KB",
        { } k and < 1024 => $"{k} KB",
        { } k => $"{k / 1024.0:0.#} MB"
    };

    // Same two colours AgentJobHistoryPage.OutcomeBrush uses for a run outcome — this provider has no
    // themed "warning"/"error" background resource, only per-status text tints applied ad hoc.
    private static IBrush? HealthBrush(string? groupSyncHealthDesc) => groupSyncHealthDesc switch
    {
        "NOT_HEALTHY" => new SolidColorBrush(Color.FromRgb(0xD6, 0x45, 0x45)),
        "PARTIALLY_HEALTHY" => new SolidColorBrush(Color.FromRgb(0xE0, 0xA3, 0x3E)),
        _ => null
    };

    private void Fail(string message)
    {
        _bannerText.Text = message;
        _bannerText.Foreground = new SolidColorBrush(Color.FromRgb(0xD6, 0x45, 0x45));
        _age.Text = $"refresh failed {DateTime.Now:T}";
    }
}
