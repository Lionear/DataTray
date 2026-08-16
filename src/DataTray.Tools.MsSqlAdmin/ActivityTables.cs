using System.Globalization;

namespace DataTray.Tools.MsSqlAdmin;

/// <summary>
/// Turns samples into the rows of the five grids. Each method is a pure function of the samples it is
/// given, so what the monitor displays can be asserted without a server.
/// </summary>
/// <remarks>
/// Column headers stay in English on purpose. They are the vocabulary of the DMVs and of SSMS itself —
/// "Wait Type", "Logical Reads/sec", "Head Blocker" — and a DBA reading a Dutch UI still reads
/// <c>sys.dm_os_wait_stats</c> in English. Everything the monitor says in its own voice (section titles,
/// the toolbar, its errors) is localised.
/// </remarks>
internal static class ActivityTables
{
    public static readonly string[] ProcessHeaders =
    [
        "Session ID", "User Process", "Login", "Database", "Task State", "Command", "Application",
        "Wait Time (ms)", "Wait Type", "Wait Resource", "Blocked By", "Head Blocker", "Memory Use (KB)",
        "Host Name", "Workload Group"
    ];

    public static readonly string[] WaitHeaders =
    [
        "Wait Category", "Wait Time (ms/sec)", "Recent Wait Time (ms/sec)", "Average Waiter Count",
        "Cumulative Wait Time (sec)"
    ];

    public static readonly string[] FileIoHeaders =
    [
        "Database", "File Name", "MB/sec Read", "MB/sec Written", "Response Time (ms)"
    ];

    public static readonly string[] RecentQueryHeaders =
    [
        "Query", "Executions/sec", "CPU (ms/sec)", "Physical Reads/sec", "Logical Writes/sec",
        "Logical Reads/sec", "Average Duration", "Plan Count", "Database"
    ];

    public static readonly string[] ActiveQueryHeaders =
    [
        "Query", "Session ID", "Database", "Elapsed Time (ms)", "CPU (ms)", "Logical Reads", "Writes",
        "Wait Type", "Blocked By"
    ];

    /// <summary>
    /// The databases present in <paramref name="rows"/>, sorted, with <paramref name="allLabel"/> first —
    /// what the Database dropdown over a grid offers. Only the databases that currently have rows are
    /// listed, so the choices are the ones that can actually change what is on screen.
    /// </summary>
    public static IReadOnlyList<string> Databases(IReadOnlyList<string[]> rows, int column, string allLabel)
    {
        var names = new List<string> { allLabel };
        if (column >= 0)
        {
            names.AddRange(rows
                .Select(row => column < row.Length ? row[column] : string.Empty)
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCulture));
        }

        return names;
    }

    /// <summary>
    /// The build-and-host line the toolbar shows, from <c>@@VERSION</c>: "Microsoft SQL Server 2025
    /// (RTM-CU6) (KB5093421) - 17.0.4055.5 (X64) · Linux (Ubuntu 24.04.4 LTS)".
    /// </summary>
    /// <remarks>
    /// <c>@@VERSION</c>'s first line is the build, and its last ends in "&lt;edition&gt; on &lt;host&gt;" —
    /// the same shape on Windows and on Linux. A string that does not have that shape (a localised install
    /// says "on" in its own language) falls back to the build alone: half the answer beats a wrong one, and
    /// the whole string is on the tooltip either way.
    /// </remarks>
    public static string ServerVersion(string version)
    {
        var build = version.Split('\n')[0].Trim();
        var on = version.LastIndexOf(" on ", StringComparison.Ordinal);
        if (on < 0)
        {
            return build;
        }

        var host = version[(on + 4)..].Trim();
        // "… on Windows Server 2019 Standard 10.0 <X64> (Build 17763: ) (Hypervisor)" — the architecture
        // and build number are already in the first line, and this line has to fit beside a toolbar.
        var architecture = host.IndexOf(" <", StringComparison.Ordinal);
        if (architecture > 0)
        {
            host = host[..architecture];
        }

        return build.Length == 0 ? host : build + " · " + host;
    }

    public static IReadOnlyList<string[]> Processes(ActivitySample now) =>
    [
        .. now.Processes.Select(p => new[]
        {
            p.SessionId.ToString(CultureInfo.CurrentCulture),
            p.IsUserProcess ? "1" : "0",
            p.Login,
            p.Database,
            p.TaskState,
            p.Command,
            p.Application,
            p.WaitTimeMs.ToString("N0", CultureInfo.CurrentCulture),
            p.WaitType,
            p.WaitResource,
            p.BlockedBy == 0 ? string.Empty : p.BlockedBy.ToString(CultureInfo.CurrentCulture),
            p.HeadBlocker ? "1" : string.Empty,
            p.MemoryKb.ToString("N0", CultureInfo.CurrentCulture),
            p.HostName,
            p.WorkloadGroup
        })
    ];

    /// <summary>
    /// Resource waits. <paramref name="previous"/> gives the last interval's rate;
    /// <paramref name="baseline"/> is an older sample (a minute or so back) and gives the "recent" column,
    /// which is that same rate averaged over a longer window — the pair is what lets a spike be told apart
    /// from a trend, which is the entire point of SSMS showing both.
    /// </summary>
    public static IReadOnlyList<string[]> ResourceWaits(
        ActivitySample now,
        ActivitySample? previous,
        ActivitySample? baseline)
    {
        var last = Lookup(previous?.Waits);
        var older = Lookup(baseline?.Waits);
        var sinceLast = Seconds(previous, now);
        var sinceBaseline = Seconds(baseline, now);

        return
        [
            .. now.Waits
                .OrderByDescending(w => Rate(w, last, sinceLast))
                .ThenBy(w => w.Category, StringComparer.CurrentCulture)
                .Select(w => new[]
                {
                    w.Category,
                    ActivityRates.Number(Rate(w, last, sinceLast)),
                    ActivityRates.Number(Rate(w, older, sinceBaseline)),
                    // Wait-milliseconds accumulated per millisecond of clock time IS the average number of
                    // tasks that were waiting over the interval — 3000 ms of waiting in a 1 s interval means
                    // three tasks waited throughout it.
                    ActivityRates.Number(
                        ActivityRates.PerSecond(w.WaitTimeMs, Total(last, w.Category), sinceLast) / 1000),
                    ActivityRates.Number(w.WaitTimeMs / 1000)
                })
        ];

        static double Rate(WaitTotals w, IReadOnlyDictionary<string, WaitTotals> before, double seconds) =>
            ActivityRates.PerSecond(w.ResourceWaitTimeMs, Resource(before, w.Category), seconds);

        static double Resource(IReadOnlyDictionary<string, WaitTotals> before, string category) =>
            before.TryGetValue(category, out var w) ? w.ResourceWaitTimeMs : 0;

        static double Total(IReadOnlyDictionary<string, WaitTotals> before, string category) =>
            before.TryGetValue(category, out var w) ? w.WaitTimeMs : 0;

        static IReadOnlyDictionary<string, WaitTotals> Lookup(IReadOnlyList<WaitTotals>? waits) =>
            waits?.ToDictionary(w => w.Category, StringComparer.Ordinal)
            ?? new Dictionary<string, WaitTotals>(StringComparer.Ordinal);
    }

    public static IReadOnlyList<string[]> DataFileIo(ActivitySample now, ActivitySample? previous)
    {
        var before = previous?.Files.ToDictionary(f => f.Database + '\n' + f.FileName, StringComparer.Ordinal)
            ?? new Dictionary<string, FileIoTotals>(StringComparer.Ordinal);
        var seconds = Seconds(previous, now);

        return
        [
            .. now.Files.Select(f =>
            {
                before.TryGetValue(f.Database + '\n' + f.FileName, out var was);
                var read = ActivityRates.PerSecond(f.BytesRead, was?.BytesRead ?? 0, seconds) / (1024 * 1024);
                var written = ActivityRates.PerSecond(f.BytesWritten, was?.BytesWritten ?? 0, seconds) / (1024 * 1024);
                // Response time is stall per I/O over the interval, not since startup: a file that was slow
                // an hour ago and is fine now should read as fine now. With no previous sample there is no
                // interval, and the cumulative totals would otherwise be reported as if they were this
                // second's — the first refresh must say nothing, like every other rate here.
                var ios = f.IoCount - (was?.IoCount ?? 0);
                var stall = f.IoStallMs - (was?.IoStallMs ?? 0);
                var response = was is not null && ios > 0 && stall >= 0 ? (double)stall / ios : 0;

                return new[]
                {
                    f.Database,
                    f.FileName,
                    ActivityRates.Number(read),
                    ActivityRates.Number(written),
                    ActivityRates.Number(response)
                };
            })
        ];
    }

    public static IReadOnlyList<string[]> RecentQueries(ActivitySample now, ActivitySample? previous)
    {
        // DistinctBy, not a plain ToDictionary: the keys come from a DMV, and a duplicate one used to take
        // the whole refresh down with "An item with the same key has already been added". The sampler now
        // aggregates so that cannot happen, but a grid that loses one row's baseline for a refresh is a far
        // better failure than a monitor that stops monitoring.
        var before = previous?.Queries.DistinctBy(q => q.Key, StringComparer.Ordinal)
                .ToDictionary(q => q.Key, StringComparer.Ordinal)
            ?? new Dictionary<string, QueryTotals>(StringComparer.Ordinal);
        var seconds = Seconds(previous, now);

        return
        [
            .. now.Queries.Select(q =>
            {
                before.TryGetValue(q.Key, out var was);
                return new[]
                {
                    Collapse(q.Text),
                    ActivityRates.Number(ActivityRates.PerSecond(q.Executions, was?.Executions ?? 0, seconds)),
                    ActivityRates.Number(
                        ActivityRates.PerSecond(q.WorkerTimeUs, was?.WorkerTimeUs ?? 0, seconds) / 1000),
                    ActivityRates.Number(ActivityRates.PerSecond(q.PhysicalReads, was?.PhysicalReads ?? 0, seconds)),
                    ActivityRates.Number(ActivityRates.PerSecond(q.LogicalWrites, was?.LogicalWrites ?? 0, seconds)),
                    ActivityRates.Number(ActivityRates.PerSecond(q.LogicalReads, was?.LogicalReads ?? 0, seconds)),
                    // Average duration is a lifetime average, as in SSMS — it answers "what does this query
                    // usually cost", which is a different question from the per-second columns beside it.
                    ActivityRates.Milliseconds(q.Executions == 0 ? 0 : q.ElapsedUs / 1000d / q.Executions),
                    q.PlanCount.ToString(CultureInfo.CurrentCulture),
                    q.Database,
                    q.Text
                };
            })
        ];
    }

    public static IReadOnlyList<string[]> ActiveQueries(ActivitySample now) =>
    [
        .. now.ActiveQueries.Select(q => new[]
        {
            Collapse(q.Text),
            q.SessionId.ToString(CultureInfo.CurrentCulture),
            q.Database,
            q.ElapsedMs.ToString("N0", CultureInfo.CurrentCulture),
            q.CpuMs.ToString("N0", CultureInfo.CurrentCulture),
            q.LogicalReads.ToString("N0", CultureInfo.CurrentCulture),
            q.Writes.ToString("N0", CultureInfo.CurrentCulture),
            q.WaitType,
            q.BlockedBy == 0 ? string.Empty : q.BlockedBy.ToString(CultureInfo.CurrentCulture),
            q.Text
        })
    ];

    /// <summary>The query column is one grid row, so a stored procedure's newlines and indentation would
    /// otherwise render as a very tall row showing one visible word. The statement keeps its own formatting
    /// in the trailing slot past the headers (<see cref="FullTextColumn"/>), which no column binds to and
    /// which the grid shows in full on a double-click.</summary>
    private static string Collapse(string sql) =>
        string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Where both query grids carry the statement as the server wrote it. Both header sets are
    /// nine long, so this one index serves them both.</summary>
    public const int FullTextColumn = 9;

    /// <summary>Wall-clock seconds between two samples; 0 when there is no earlier one, which
    /// <see cref="ActivityRates.PerSecond"/> reads as "no rate yet" rather than as a division by zero.</summary>
    private static double Seconds(ActivitySample? before, ActivitySample now) =>
        before is null ? 0 : (now.TakenAt - before.TakenAt).TotalSeconds;
}
