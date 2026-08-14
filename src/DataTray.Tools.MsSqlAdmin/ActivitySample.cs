namespace DataTray.Tools.MsSqlAdmin;

/// <summary>One session in the Processes grid. Column-for-column what SSMS's Activity Monitor shows, with
/// <see cref="HeadBlocker"/> filled in by <see cref="ActivityRates.HeadBlockers"/> rather than by the
/// server.</summary>
internal sealed record ProcessRow(
    int SessionId,
    bool IsUserProcess,
    string Login,
    string Database,
    string TaskState,
    string Command,
    string Application,
    long WaitTimeMs,
    string WaitType,
    string WaitResource,
    int BlockedBy,
    long MemoryKb,
    string HostName,
    string WorkloadGroup)
{
    public bool HeadBlocker { get; init; }
}

/// <summary>Cumulative wait totals for one SSMS wait category, summed from <c>sys.dm_os_wait_stats</c>.
/// <see cref="ResourceWaitTimeMs"/> is wait time minus signal wait — time spent waiting for a resource
/// rather than for a scheduler to pick the task back up, which is what the grid's rate columns report.</summary>
internal sealed record WaitTotals(string Category, double WaitTimeMs, double ResourceWaitTimeMs, long WaitingTasks);

/// <summary>Cumulative I/O totals for one database file, from <c>sys.dm_io_virtual_file_stats</c>.</summary>
internal sealed record FileIoTotals(
    string Database,
    string FileName,
    long BytesRead,
    long BytesWritten,
    long IoStallMs,
    long IoCount);

/// <summary>Cumulative execution totals for one statement, summed over every cached plan for it, from
/// <c>sys.dm_exec_query_stats</c>. <see cref="Key"/> is the statement's handle and offsets — the columns the
/// sampler groups on, so it is unique within a sample — and identifies the same statement across refreshes
/// so its rates can be differenced; it changes when the statement is recompiled at a different offset, which
/// <see cref="ActivityRates.PerSecond"/> absorbs as a counter reset.</summary>
internal sealed record QueryTotals(
    string Key,
    string Text,
    string Database,
    long Executions,
    long WorkerTimeUs,
    long PhysicalReads,
    long LogicalWrites,
    long LogicalReads,
    long ElapsedUs,
    int PlanCount);

/// <summary>One currently-executing request. Point-in-time, so it needs no differencing.</summary>
internal sealed record ActiveQueryRow(
    int SessionId,
    string Database,
    string Text,
    long ElapsedMs,
    long CpuMs,
    long LogicalReads,
    long Writes,
    string WaitType,
    int BlockedBy);

/// <summary>The three instance-wide numbers behind the Overview graphs. <see cref="CpuPercent"/> is null
/// where the scheduler ring buffer is unavailable (Azure SQL Database), in which case that graph stays
/// empty rather than drawing a zero line that would read as "idle".</summary>
internal sealed record ServerCounters(double? CpuPercent, int WaitingTasks, long BatchRequests);

/// <summary>
/// One complete Activity Monitor refresh. Everything here is <b>raw</b>: cumulative counters as the server
/// reports them, never rates. Turning two samples into the per-second figures on screen is
/// <see cref="ActivityRates"/>'s job, and keeping that split is what lets the rates be tested without a
/// database.
/// </summary>
internal sealed record ActivitySample(
    DateTimeOffset TakenAt,
    IReadOnlyList<ProcessRow> Processes,
    IReadOnlyList<WaitTotals> Waits,
    IReadOnlyList<FileIoTotals> Files,
    IReadOnlyList<QueryTotals> Queries,
    IReadOnlyList<ActiveQueryRow> ActiveQueries,
    ServerCounters Counters,
    /// <summary><c>@@VERSION</c> verbatim: the build, and the operating system it runs on.
    /// <see cref="ActivityTables.ServerVersion"/> cuts it down to the line the toolbar shows.</summary>
    string Version);
