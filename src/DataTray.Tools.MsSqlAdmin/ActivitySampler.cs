using System.Globalization;
using DataTray.Sdk;

namespace DataTray.Tools.MsSqlAdmin;

/// <summary>
/// Reads one <see cref="ActivitySample"/> from the DMVs. Read-only throughout: every statement here is a
/// SELECT against a <c>sys.dm_*</c> view, which is why the monitor needs nothing beyond the VIEW SERVER
/// STATE that any of these DMVs already requires.
/// </summary>
/// <remarks>
/// <para>All eight statements go over as <b>one script</b> (<see cref="IDbProvider.ExecuteScriptAsync"/>,
/// which returns one result set per SELECT, in order) rather than as eight calls. The provider opens a
/// connection per call, and eight connections every ten seconds is a cost the server pays for nothing —
/// the samples are also then consistent with each other, taken at one instant rather than smeared across
/// eight round trips.</para>
/// <para>ponytail: one script means one failure — an instance missing any single DMV (Azure SQL Database
/// has no scheduler ring buffer and no <c>sys.master_files</c> in the shape used here) fails the whole
/// refresh rather than one section. Split the script per section if Azure SQL support is wanted; against
/// a real SQL Server instance every DMV here is present from 2012 on.</para>
/// </remarks>
internal sealed class ActivitySampler(IDbProvider provider, ConnectionProfile profile)
{
    /// <summary>How far back "recent" reaches for the expensive-queries grid. SSMS uses a fixed 30-second
    /// window; a longer refresh interval widens it to at least the interval, otherwise the grid would go
    /// blank between refreshes on a server that runs one heavy query a minute.</summary>
    public static int RecentWindowSeconds(int refreshSeconds) => Math.Max(30, refreshSeconds);

    public async Task<ActivitySample> ReadAsync(int refreshSeconds, CancellationToken ct)
    {
        var results = await provider.ExecuteScriptAsync(profile, Script(RecentWindowSeconds(refreshSeconds)), ct);
        if (results.Count < 8)
        {
            throw new InvalidOperationException(
                $"The Activity Monitor expected 8 result sets from its DMV script and received {results.Count}.");
        }

        var processes = ReadProcesses(results[0]);
        return new ActivitySample(
            DateTimeOffset.UtcNow,
            processes,
            ReadWaits(results[1]),
            ReadFiles(results[2]),
            ReadQueries(results[3]),
            ReadActiveQueries(results[4]),
            ReadCounters(results[5], results[6]),
            results[7].Rows.Count > 0 ? Text(results[7].Rows[0][0]) : string.Empty);
    }

    private static IReadOnlyList<ProcessRow> ReadProcesses(QueryResult result)
    {
        var rows = result.Rows.Select(r => new ProcessRow(
            Int(r[0]),
            Int(r[1]) != 0,
            Text(r[2]),
            Text(r[3]),
            Text(r[4]),
            Text(r[5]),
            Text(r[6]),
            Long(r[7]),
            Text(r[8]),
            Text(r[9]),
            Int(r[10]),
            // memory_usage counts 8-KB pages; SSMS's "Memory Use" column is KB.
            Long(r[11]) * 8,
            Text(r[12]),
            Text(r[13]))).ToList();

        var heads = ActivityRates.HeadBlockers(rows.Select(p => (p.SessionId, p.BlockedBy)));
        return [.. rows.Select(p => p with { HeadBlocker = heads.Contains(p.SessionId) })];
    }

    // Wait types arrive one row each and are folded into the ten SSMS categories here, so the category
    // mapping and the benign-wait filter live in one place (ActivityRates) rather than half in T-SQL.
    private static IReadOnlyList<WaitTotals> ReadWaits(QueryResult result)
    {
        var byCategory = new Dictionary<string, WaitTotals>(StringComparer.Ordinal);
        foreach (var row in result.Rows)
        {
            var type = Text(row[0]);
            if (ActivityRates.IsBenignWait(type))
            {
                continue;
            }

            var waitMs = Long(row[1]);
            var signalMs = Long(row[2]);
            var tasks = Long(row[3]);
            var category = ActivityRates.WaitCategory(type);

            byCategory[category] = byCategory.TryGetValue(category, out var running)
                ? running with
                {
                    WaitTimeMs = running.WaitTimeMs + waitMs,
                    // Signal wait is time on the runnable queue after the resource was granted; SSMS's
                    // resource-wait columns exclude it, or a CPU-starved server reads as a lock problem.
                    ResourceWaitTimeMs = running.ResourceWaitTimeMs + Math.Max(waitMs - signalMs, 0),
                    WaitingTasks = running.WaitingTasks + tasks
                }
                : new WaitTotals(category, waitMs, Math.Max(waitMs - signalMs, 0), tasks);
        }

        return [.. byCategory.Values];
    }

    private static IReadOnlyList<FileIoTotals> ReadFiles(QueryResult result) =>
    [
        .. result.Rows.Select(r => new FileIoTotals(
            Text(r[0]),
            Text(r[1]),
            Long(r[2]),
            Long(r[3]),
            Long(r[4]),
            Long(r[5])))
    ];

    private static IReadOnlyList<QueryTotals> ReadQueries(QueryResult result) =>
    [
        .. result.Rows.Select(r => new QueryTotals(
            Text(r[0]),
            Collapse(Text(r[1])),
            Text(r[2]),
            Long(r[3]),
            Long(r[4]),
            Long(r[5]),
            Long(r[6]),
            Long(r[7]),
            Long(r[8]),
            Int(r[9])))
    ];

    private static IReadOnlyList<ActiveQueryRow> ReadActiveQueries(QueryResult result) =>
    [
        .. result.Rows.Select(r => new ActiveQueryRow(
            Int(r[0]),
            Text(r[1]),
            Collapse(Text(r[2])),
            Long(r[3]),
            Long(r[4]),
            Long(r[5]),
            Long(r[6]),
            Text(r[7]),
            Int(r[8])))
    ];

    private static ServerCounters ReadCounters(QueryResult counters, QueryResult waitingTasks)
    {
        var row = counters.Rows.Count > 0 ? counters.Rows[0] : [null, null];
        var waiting = waitingTasks.Rows.Count(r => !ActivityRates.IsBenignWait(Text(r[0])));

        return new ServerCounters(
            // Null where the resource-pool counters are absent, so the graph shows a gap rather than a
            // zero line that would read as an idle instance.
            row[0] is null ? null : Convert.ToDouble(row[0], CultureInfo.InvariantCulture),
            waiting,
            row.Length > 1 ? Long(row[1]) : 0);
    }

    // Query text is shown one row per query, so the newlines and indentation of a stored procedure would
    // otherwise render as a single very tall row with one visible word.
    private static string Collapse(string sql) =>
        string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Text(object? value) => value?.ToString() ?? string.Empty;

    private static long Long(object? value) => value is null
        ? 0
        : Convert.ToInt64(value, CultureInfo.InvariantCulture);

    private static int Int(object? value) => value is null
        ? 0
        : Convert.ToInt32(value, CultureInfo.InvariantCulture);

    /// <summary>
    /// The eight SELECTs behind one refresh, in the order <see cref="ReadAsync"/> unpacks them. Kept as one
    /// literal so the column order a reader sees and the indexes the reader uses sit on the same screen.
    /// </summary>
    private static string Script(int recentWindowSeconds) => $"""
        -- DataTray Activity Monitor. This marker is what result set 4 filters on: all eight statements go
        -- over as one batch and therefore share one sql_handle and one cached text, so a single NOT LIKE
        -- keeps the monitor's own queries out of its Recent Expensive Queries grid — which would otherwise
        -- report, every ten seconds, that the most expensive thing on this server is the monitor.

        -- 1. Processes: one row per session, with the request it is running (if any).
        SELECT s.session_id,
               s.is_user_process,
               ISNULL(s.login_name, '') AS login_name,
               ISNULL(DB_NAME(COALESCE(r.database_id, NULLIF(s.database_id, 0))), '') AS database_name,
               ISNULL(COALESCE(r.status, s.status), '') AS task_state,
               ISNULL(r.command, '') AS command,
               ISNULL(s.program_name, '') AS program_name,
               ISNULL(r.wait_time, 0) AS wait_time,
               ISNULL(r.wait_type, '') AS wait_type,
               ISNULL(r.wait_resource, '') AS wait_resource,
               ISNULL(r.blocking_session_id, 0) AS blocking_session_id,
               s.memory_usage,
               ISNULL(s.host_name, '') AS host_name,
               ISNULL(wg.name, '') AS workload_group
        FROM sys.dm_exec_sessions AS s
        LEFT JOIN sys.dm_exec_requests AS r ON r.session_id = s.session_id
        LEFT JOIN sys.dm_resource_governor_workload_groups AS wg ON wg.group_id = s.group_id
        ORDER BY s.session_id;

        -- 2. Resource waits: cumulative since startup, categorised and filtered by the client.
        SELECT wait_type, wait_time_ms, signal_wait_time_ms, waiting_tasks_count
        FROM sys.dm_os_wait_stats
        WHERE wait_time_ms > 0;

        -- 3. Data file I/O: cumulative bytes and stall per file.
        SELECT ISNULL(DB_NAME(vfs.database_id), '') AS database_name,
               mf.physical_name,
               vfs.num_of_bytes_read,
               vfs.num_of_bytes_written,
               vfs.io_stall,
               vfs.num_of_reads + vfs.num_of_writes AS io_count
        FROM sys.dm_io_virtual_file_stats(NULL, NULL) AS vfs
        JOIN sys.master_files AS mf
          ON mf.database_id = vfs.database_id AND mf.file_id = vfs.file_id;

        -- 4. Recent expensive queries: statements executed inside the window, dearest first. plan_count is
        --    every cached plan for the same query_hash, which is what makes plan-cache bloat visible.
        --    dm_exec_query_stats has one row per *plan*, so one statement can appear several times over:
        --    the same batch compiled against three databases, a recompile under different SET options, a
        --    serial and a parallel plan side by side. Those rows are summed into one per statement here.
        --    Two reasons: the grid would otherwise show the same query several times with its cost split
        --    across the rows, and query_key — which the client uses to difference two samples — has to be
        --    unique or the refresh dies on a duplicate dictionary key. The GROUP BY is therefore exactly
        --    the three columns query_key is built from, which is what makes it unique by construction.
        WITH plans AS (
            SELECT query_hash, COUNT(*) AS plan_count
            FROM sys.dm_exec_query_stats
            GROUP BY query_hash),
        recent AS (
            SELECT qs.sql_handle,
                   qs.statement_start_offset,
                   qs.statement_end_offset,
                   -- Every plan of one statement carries the same query_hash (they are the same parse
                   -- tree); MAX only picks the one value out of the group.
                   MAX(qs.query_hash) AS query_hash,
                   SUM(qs.execution_count) AS execution_count,
                   SUM(qs.total_worker_time) AS total_worker_time,
                   SUM(qs.total_physical_reads) AS total_physical_reads,
                   SUM(qs.total_logical_writes) AS total_logical_writes,
                   SUM(qs.total_logical_reads) AS total_logical_reads,
                   SUM(qs.total_elapsed_time) AS total_elapsed_time
            FROM sys.dm_exec_query_stats AS qs
            WHERE qs.last_execution_time > DATEADD(second, -{recentWindowSeconds}, GETDATE())
            GROUP BY qs.sql_handle, qs.statement_start_offset, qs.statement_end_offset)
        SELECT TOP (50)
               CONVERT(varchar(140), r.sql_handle, 2) + '-'
                   + CONVERT(varchar(12), r.statement_start_offset) + '-'
                   + CONVERT(varchar(12), r.statement_end_offset) AS query_key,
               SUBSTRING(st.text, (r.statement_start_offset / 2) + 1,
                   ((CASE r.statement_end_offset
                         WHEN -1 THEN DATALENGTH(st.text)
                         ELSE r.statement_end_offset END - r.statement_start_offset) / 2) + 1) AS statement_text,
               ISNULL(DB_NAME(st.dbid), '') AS database_name,
               r.execution_count,
               r.total_worker_time,
               r.total_physical_reads,
               r.total_logical_writes,
               r.total_logical_reads,
               r.total_elapsed_time,
               ISNULL(p.plan_count, 1) AS plan_count
        FROM recent AS r
        CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) AS st
        LEFT JOIN plans AS p ON p.query_hash = r.query_hash
        WHERE st.text NOT LIKE '%DataTray Activity Monitor%'
        ORDER BY r.total_worker_time DESC;

        -- 5. Active expensive queries: what is executing right now, this connection excluded.
        SELECT r.session_id,
               ISNULL(DB_NAME(r.database_id), '') AS database_name,
               SUBSTRING(st.text, (r.statement_start_offset / 2) + 1,
                   ((CASE r.statement_end_offset
                         WHEN -1 THEN DATALENGTH(st.text)
                         ELSE r.statement_end_offset END - r.statement_start_offset) / 2) + 1) AS statement_text,
               r.total_elapsed_time,
               r.cpu_time,
               r.logical_reads,
               r.writes,
               ISNULL(r.wait_type, '') AS wait_type,
               ISNULL(r.blocking_session_id, 0) AS blocking_session_id
        FROM sys.dm_exec_requests AS r
        CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) AS st
        WHERE r.session_id <> @@SPID
        ORDER BY r.cpu_time DESC;

        -- 6. Overview counters. The resource-pool CPU counter is a raw fraction over a fixed window: the
        --    used value against its base is the instance's share of the whole box, which is what SSMS
        --    graphs as "% Processor Time" — one busy core of sixteen reads as 6%. The base is the same
        --    window for every pool, so it is taken once (MAX) while the usage is summed across pools.
        --    Batch Requests/sec, by contrast, IS cumulative and is differenced by the client.
        --    object_name is matched with LIKE because a named instance prefixes it.
        SELECT (SELECT SUM(CASE WHEN counter_name = 'CPU usage %' THEN cntr_value END) * 100.0
                       / NULLIF(MAX(CASE WHEN counter_name = 'CPU usage % base' THEN cntr_value END), 0)
                FROM sys.dm_os_performance_counters
                WHERE object_name LIKE '%Resource Pool Stats%') AS cpu_percent,
               (SELECT TOP (1) cntr_value
                FROM sys.dm_os_performance_counters
                WHERE counter_name = 'Batch Requests/sec'
                  AND object_name LIKE '%SQL Statistics%') AS batch_requests;

        -- 7. Waiting tasks, one row per waiting request, so the client can drop the idle waits with the
        --    same list the Resource Waits grid uses.
        SELECT ISNULL(wait_type, '') AS wait_type
        FROM sys.dm_os_waiting_tasks
        WHERE session_id IS NOT NULL;

        -- 8. Build and host, for the line beside the toolbar. @@VERSION rather than SERVERPROPERTY plus
        --    sys.dm_os_host_info because it names the host in the same breath as the build and exists on
        --    every version — host_info only arrived in 2017, and a missing view here would fail the whole
        --    refresh (see the remarks on this class), not just this one line.
        SELECT @@VERSION AS server_version;
        """;
}
