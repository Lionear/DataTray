using System.Globalization;

namespace DataTray.Tools.MsSqlAdmin;

/// <summary>
/// The arithmetic behind every figure in the Activity Monitor that is not simply a column of a DMV.
///
/// <para>SQL Server's monitoring DMVs are almost all <b>cumulative since startup</b> — wait time, bytes
/// read, execution counts. SSMS turns those into the per-second rates it displays by taking the difference
/// between two samples and dividing by the wall-clock time between them, and this does the same. It is
/// pure and side-effect free on purpose: it is the only part of the monitor that can be wrong in a way a
/// database cannot tell you about, so it is the part with tests.</para>
/// </summary>
internal static class ActivityRates
{
    /// <summary>
    /// A counter's change per second between two samples. Returns 0 rather than a negative or infinite
    /// rate when the counter went backwards or no time passed: a restarted instance, a
    /// <c>DBCC SQLPERF(N'sys.dm_os_wait_stats', CLEAR)</c>, a failed-over replica and a plan evicted and
    /// re-cached all reset these counters, and a monitor that reacts by printing a huge negative number
    /// is reporting on itself rather than on the server.
    /// </summary>
    public static double PerSecond(double now, double before, double seconds) =>
        seconds <= 0 || now < before ? 0 : (now - before) / seconds;

    /// <summary>
    /// The instance's share of the whole machine's CPU over the interval between two samples, as SSMS's
    /// "% Processor Time" graph reports it: one core of sixteen fully busy is 6%, not 100%. Null when
    /// there is nothing to difference yet (the first refresh), when the clock did not move, or when the
    /// server did not say how many CPUs it has — the graph then shows a gap rather than a zero line that
    /// would read as an idle instance.
    /// </summary>
    /// <remarks>
    /// The inputs are <c>sys.dm_os_sys_info</c>'s own process accounting rather than a perfmon counter or
    /// the scheduler ring buffer, because those two do not exist in the same form on both platforms: the
    /// ring buffer is Windows-only, and the Resource Pool Stats "CPU usage %" counter reads flat zero on
    /// some builds of SQL Server on Linux (SE-260). Kernel and user milliseconds against the wall clock are
    /// what the OS tells the engine on either.
    /// </remarks>
    public static double? ProcessorTime(ServerCounters now, ServerCounters? before)
    {
        if (before is null || now.CpuCount <= 0)
        {
            return null;
        }

        var elapsed = now.MsTicks - before.MsTicks;
        var burnt = now.ProcessCpuMs - before.ProcessCpuMs;
        if (elapsed <= 0 || burnt < 0)
        {
            // The instance restarted between samples (ms_ticks is machine uptime, the CPU total is not).
            return null;
        }

        // A sample straddling a restart, or a clock nudged backwards, can put this over 100; the graph is
        // drawn against a fixed 100 scale and a spike beyond it is noise, not information.
        return Math.Min(burnt * 100.0 / elapsed / now.CpuCount, 100);
    }

    /// <summary>
    /// The sessions at the head of a blocking chain: each one blocks at least one other session and is not
    /// itself blocked. This is what SSMS's "Head Blocker" column marks — the session you would actually
    /// kill, as opposed to the ones merely queued behind it.
    /// </summary>
    /// <remarks>
    /// Computed here rather than in T-SQL because the blocking graph is already in the Processes result:
    /// a recursive CTE over <c>sys.dm_exec_requests</c> would re-read, on the server, a relation we hold.
    /// Self-blocking (a session blocked by itself, which happens on parallel queries waiting on an
    /// exchange) does not make a session blocked for this purpose — otherwise a parallel query at the head
    /// of a chain would never be marked.
    /// </remarks>
    public static IReadOnlySet<int> HeadBlockers(IEnumerable<(int SessionId, int BlockedBy)> sessions)
    {
        var rows = sessions as IReadOnlyCollection<(int SessionId, int BlockedBy)> ?? [.. sessions];
        var blocked = rows
            .Where(r => r.BlockedBy != 0 && r.BlockedBy != r.SessionId)
            .Select(r => r.SessionId)
            .ToHashSet();

        return rows
            .Select(r => r.BlockedBy)
            .Where(b => b != 0 && !blocked.Contains(b))
            .ToHashSet();
    }

    /// <summary>
    /// The SSMS Resource Waits category a wait type belongs to. SSMS groups several hundred wait types
    /// into a handful of buckets so the grid says "Buffer I/O" rather than listing PAGEIOLATCH_SH,
    /// PAGEIOLATCH_EX and six others separately; anything unrecognised lands in "Other", which is why the
    /// list does not need to be exhaustive to be correct.
    /// </summary>
    public static string WaitCategory(string waitType)
    {
        var type = waitType.ToUpperInvariant();

        // Order matters: PAGEIOLATCH_* is buffer I/O (a page being fetched from disk) while PAGELATCH_* is
        // a memory-only latch on a page already in the pool, and the prefixes overlap.
        if (type.StartsWith("PAGEIOLATCH", StringComparison.Ordinal))
        {
            return "Buffer I/O";
        }

        if (type.StartsWith("PAGELATCH", StringComparison.Ordinal))
        {
            return "Buffer Latch";
        }

        if (type.StartsWith("LCK_", StringComparison.Ordinal))
        {
            return "Lock";
        }

        if (type.StartsWith("LATCH_", StringComparison.Ordinal))
        {
            return "Latch";
        }

        if (type is "RESOURCE_SEMAPHORE_QUERY_COMPILE" or "RESOURCE_SEMAPHORE_SMALL_QUERY")
        {
            return "Compilation";
        }

        return type switch
        {
            "RESOURCE_SEMAPHORE" or "CMEMTHREAD" or "SOS_RESERVEDMEMBLOCKLIST" or "MEMORY_ALLOCATION_EXT"
                or "RESERVED_MEMORY_ALLOCATION_EXT" or "UTIL_PAGE_ALLOC" => "Memory",

            "WRITELOG" or "LOGBUFFER" or "LOGMGR" or "LOGMGR_FLUSH" or "LOGMGR_RESERVE_APPEND"
                or "LOG_RATE_GOVERNOR" or "CHKPT" or "BACKUPTHREAD_LOG" => "Logging",

            "ASYNC_NETWORK_IO" or "NET_WAITFOR_PACKET" or "PROXY_NETWORK_IO"
                or "EXTERNAL_SCRIPT_NETWORK_IOF" => "Network I/O",

            "BACKUPBUFFER" or "BACKUPIO" or "BACKUPTHREAD" => "Backup",

            _ => "Other"
        };
    }

    /// <summary>
    /// Wait types that mean "a background task is idle", not "work is waiting". They dwarf every real wait
    /// — a server doing nothing for a week accumulates a week of LAZYWRITER_SLEEP — so counting them would
    /// leave the Resource Waits grid permanently reporting sleep as the server's main activity. SSMS
    /// filters the same family out.
    /// </summary>
    public static bool IsBenignWait(string waitType) => BenignWaits.Contains(waitType);

    private static readonly HashSet<string> BenignWaits = new(StringComparer.OrdinalIgnoreCase)
    {
        "BROKER_EVENTHANDLER", "BROKER_RECEIVE_WAITFOR", "BROKER_TASK_STOP", "BROKER_TO_FLUSH",
        "BROKER_TRANSMITTER", "CHECKPOINT_QUEUE", "CHKPT", "CLR_AUTO_EVENT", "CLR_MANUAL_EVENT",
        "CLR_SEMAPHORE", "DBMIRROR_DBM_EVENT", "DBMIRROR_EVENTS_QUEUE", "DBMIRROR_WORKER_QUEUE",
        "DBMIRRORING_CMD", "DIRTY_PAGE_POLL", "DISPATCHER_QUEUE_SEMAPHORE", "FT_IFTS_SCHEDULER_IDLE_WAIT",
        "FT_IFTSHC_MUTEX", "HADR_CLUSAPI_CALL", "HADR_FILESTREAM_IOMGR_IOCOMPLETION", "HADR_LOGCAPTURE_WAIT",
        "HADR_NOTIFICATION_DEQUEUE", "HADR_TIMER_TASK", "HADR_WORK_QUEUE", "KSOURCE_WAKEUP",
        "LAZYWRITER_SLEEP", "LOGMGR_QUEUE", "ONDEMAND_TASK_QUEUE", "PARALLEL_REDO_DRAIN_WORKER",
        "PARALLEL_REDO_LOG_CACHE", "PARALLEL_REDO_TRAN_LIST", "PARALLEL_REDO_WORKER_SYNC",
        "PARALLEL_REDO_WORKER_WAIT_WORK", "PREEMPTIVE_XE_GETTARGETSTATE", "PWAIT_ALL_COMPONENTS_INITIALIZED",
        "QDS_ASYNC_QUEUE", "QDS_PERSIST_TASK_MAIN_LOOP_SLEEP", "QDS_SHUTDOWN_QUEUE", "REDO_THREAD_PENDING_WORK",
        "REQUEST_FOR_DEADLOCK_SEARCH", "SLEEP_SYSTEMTASK", "SLEEP_TASK", "SLEEP_DBSTARTUP", "SLEEP_DCOMSTARTUP",
        "SLEEP_MASTERDBREADY", "SLEEP_MASTERMDREADY", "SLEEP_MASTERUPGRADED", "SLEEP_MSDBSTARTUP",
        "SLEEP_TEMPDBSTARTUP", "SNI_HTTP_ACCEPT", "SP_SERVER_DIAGNOSTICS_SLEEP", "SQLTRACE_BUFFER_FLUSH",
        "SQLTRACE_INCREMENTAL_FLUSH_SLEEP", "SQLTRACE_WAIT_ENTRIES", "WAIT_FOR_RESULTS", "WAITFOR",
        "WAITFOR_TASKSHUTDOWN", "WAIT_XTP_HOST_WAIT", "WAIT_XTP_OFFLINE_CKPT_NEW_LOG", "WAIT_XTP_CKPT_CLOSE",
        "WAIT_XTP_RECOVERY", "XE_BUFFERMGR_ALLPROCESSED_EVENT", "XE_DISPATCHER_JOIN", "XE_DISPATCHER_WAIT",
        "XE_LIVE_TARGET_TVF", "XE_TIMER_EVENT"
    };

    /// <summary>A number for a grid cell: no thousands noise below 10, one decimal above zero, and a plain
    /// "0" for nothing at all — so a quiet server reads as quiet rather than as a wall of "0.00".</summary>
    public static string Number(double value) => value == 0
        ? "0"
        : value.ToString(value >= 100 ? "N0" : "N1", CultureInfo.CurrentCulture);

    /// <summary>A duration in milliseconds, rendered the way SSMS's Average Duration column reads.</summary>
    public static string Milliseconds(double ms) => Number(ms) + " ms";

    /// <summary>
    /// Orders two grid cells the way a header click should: numbers by value, so 9 comes before 10 and
    /// 1,234 after both, and everything else as text.
    /// </summary>
    /// <remarks>
    /// The cells arrive already formatted for display, so the parse has to accept what
    /// <see cref="Number"/> and <see cref="Milliseconds"/> put there — the culture's thousands separator,
    /// and a unit appended after a space ("200 ms"), which is otherwise a column of numbers sorted as
    /// text with 1,234 ms filed between 1 ms and 2 ms.
    ///
    /// <para>Numbers sort ahead of text rather than being compared against it. A column can hold both (an
    /// empty "Blocked By" beside a session id), and comparing each pair by whichever rule happens to fit
    /// is not a total order: 9 &lt; 10 as numbers while "1x" falls between them as text, and a sort given
    /// contradictory answers is entitled to throw — which would leave a header click doing nothing at
    /// all.</para>
    /// </remarks>
    public static int CompareCells(string a, string b)
    {
        var left = TryNumber(a, out var x);
        var right = TryNumber(b, out var y);

        if (left && right)
        {
            return x.CompareTo(y);
        }

        return left == right
            ? string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase)
            : left ? -1 : 1;
    }

    private static bool TryNumber(string cell, out double value) =>
        double.TryParse(cell, NumberStyles.Any, CultureInfo.CurrentCulture, out value)
        || (cell.IndexOf(' ') is > 0 and var unit
            && double.TryParse(cell.AsSpan(0, unit), NumberStyles.Any, CultureInfo.CurrentCulture, out value));
}
