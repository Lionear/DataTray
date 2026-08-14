using DataTray.Tools.MsSqlAdmin;

namespace DataTray.Tools.MsSqlAdmin.Tests;

/// <summary>
/// The grids themselves: two samples ten seconds apart must produce the per-second figures SSMS would show
/// for the same pair.
/// </summary>
public class ActivityTablesTests
{
    private static readonly DateTimeOffset First = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Second = First.AddSeconds(10);

    [Fact]
    public void DataFileIo_reports_megabytes_per_second_and_stall_per_io()
    {
        var before = Sample(First, files: [new FileIoTotals("Sales", "S.mdf", 0, 0, 0, 0)]);
        // 20 MB read and 10 MB written in ten seconds, over 200 I/Os that stalled 1000 ms in total.
        var now = Sample(Second, files:
        [
            new FileIoTotals("Sales", "S.mdf", 20 * 1024 * 1024, 10 * 1024 * 1024, 1_000, 200)
        ]);

        var row = ActivityTables.DataFileIo(now, before).Single();

        Assert.Equal("Sales", row[0]);
        Assert.Equal(ActivityRates.Number(2), row[2]);
        Assert.Equal(ActivityRates.Number(1), row[3]);
        Assert.Equal(ActivityRates.Number(5), row[4]);
    }

    [Fact]
    public void DataFileIo_response_time_is_measured_over_the_interval_not_since_startup()
    {
        // The file stalled badly in the past (9 s over 100 I/Os) but not at all since the last sample. A
        // response time computed from the cumulative totals would keep reporting the old problem forever.
        var before = Sample(First, files: [new FileIoTotals("Sales", "S.mdf", 0, 0, 9_000, 100)]);
        var now = Sample(Second, files: [new FileIoTotals("Sales", "S.mdf", 0, 0, 9_000, 150)]);

        var row = ActivityTables.DataFileIo(now, before).Single();

        Assert.Equal("0", row[4]);
    }

    [Fact]
    public void RecentQueries_turn_microseconds_of_cpu_into_milliseconds_per_second()
    {
        var before = Sample(First, queries: [Query(executions: 10, workerTimeUs: 1_000_000, elapsedUs: 2_000_000)]);
        // Another 20 executions and 2 s of CPU in the ten-second interval: 2 executions/sec, 200 ms/sec.
        var now = Sample(Second, queries: [Query(executions: 30, workerTimeUs: 3_000_000, elapsedUs: 6_000_000)]);

        var row = ActivityTables.RecentQueries(now, before).Single();

        Assert.Equal(ActivityRates.Number(2), row[1]);
        Assert.Equal(ActivityRates.Number(200), row[2]);
        // Average duration stays a lifetime average, as in SSMS: 6 s over 30 executions is 200 ms.
        Assert.Equal(ActivityRates.Milliseconds(200), row[6]);
    }

    [Fact]
    public void RecentQueries_survive_a_previous_sample_that_repeats_a_key()
    {
        // dm_exec_query_stats has a row per plan, so one statement can come back twice; the sampler sums
        // those, but a server that finds a way to repeat a key must not take the refresh down with
        // "An item with the same key has already been added" — which is exactly how this was reported.
        var before = Sample(First, queries:
        [
            Query(executions: 10, workerTimeUs: 1_000_000, elapsedUs: 2_000_000),
            Query(executions: 4, workerTimeUs: 500_000, elapsedUs: 1_000_000)
        ]);
        var now = Sample(Second, queries: [Query(executions: 30, workerTimeUs: 3_000_000, elapsedUs: 6_000_000)]);

        var row = ActivityTables.RecentQueries(now, before).Single();

        Assert.Equal(ActivityRates.Number(2), row[1]);
    }

    [Fact]
    public void The_first_refresh_shows_no_rates_at_all()
    {
        // With one sample there is nothing to difference. Zero is the honest answer; the alternative is
        // treating "since the server started" as "in the last ten seconds", which reads as a catastrophe.
        var now = Sample(First, files: [new FileIoTotals("Sales", "S.mdf", 500L * 1024 * 1024, 0, 60_000, 10_000)]);

        var row = ActivityTables.DataFileIo(now, previous: null).Single();

        Assert.Equal("0", row[2]);
        Assert.Equal("0", row[3]);
        // Response time too: 60 s of stall over 10 000 lifetime I/Os is 6 ms per I/O since the server
        // started, and reporting that as the file's current response time is how the first refresh ends up
        // accusing a healthy disk. Caught by rendering the tab, not by any query check.
        Assert.Equal("0", row[4]);
    }

    [Fact]
    public void ResourceWaits_report_resource_time_excluding_the_signal_wait()
    {
        // 10 s of lock wait of which 2 s was signal wait (waiting for a CPU after the lock was granted).
        // The grid must report the 8 s of resource wait, or a CPU-starved server reads as a lock problem.
        var before = Sample(First, waits: [new WaitTotals("Lock", 0, 0, 0)]);
        var now = Sample(Second, waits: [new WaitTotals("Lock", 10_000, 8_000, 5)]);

        var row = ActivityTables.ResourceWaits(now, before, before).Single();

        Assert.Equal("Lock", row[0]);
        Assert.Equal(ActivityRates.Number(800), row[1]);
        // 10 000 ms of waiting over a 10 s interval is an average of one task waiting throughout.
        Assert.Equal(ActivityRates.Number(1), row[3]);
        Assert.Equal(ActivityRates.Number(10), row[4]);
    }

    [Fact]
    public void Processes_mark_the_head_blocker_and_leave_an_unblocked_row_empty()
    {
        var now = Sample(First, processes: WithHeadBlockers([Process(51, blockedBy: 0), Process(52, blockedBy: 51)]));

        var rows = ActivityTables.Processes(now);

        Assert.Equal("1", rows[0][11]);
        Assert.Equal(string.Empty, rows[1][11]);
        Assert.Equal("51", rows[1][10]);
    }

    [Fact]
    public void ServerVersion_names_the_build_and_the_host_on_linux()
    {
        // Verbatim from SQL Server 2025 CU6 in a container, tabs and all.
        var line = ActivityTables.ServerVersion(
            "Microsoft SQL Server 2025 (RTM-CU6) (KB5093421) - 17.0.4055.5 (X64) \n"
            + "\tJun  9 2026 12:41:10 \n"
            + "\tCopyright (C) 2025 Microsoft Corporation\n"
            + "\tEnterprise Developer Edition (64-bit) on Linux (Ubuntu 24.04.4 LTS) <X64>");

        Assert.Equal(
            "Microsoft SQL Server 2025 (RTM-CU6) (KB5093421) - 17.0.4055.5 (X64) · Linux (Ubuntu 24.04.4 LTS)",
            line);
    }

    [Fact]
    public void ServerVersion_drops_the_architecture_tail_windows_appends()
    {
        var line = ActivityTables.ServerVersion(
            "Microsoft SQL Server 2019 (RTM-CU18) (KB5017593) - 15.0.4261.1 (X64) \r\n"
            + "\tSep  6 2022 20:09:11 \r\n"
            + "\tCopyright (C) 2019 Microsoft Corporation\r\n"
            + "\tDeveloper Edition (64-bit) on Windows Server 2019 Standard 10.0 <X64> (Build 17763: ) (Hypervisor)");

        Assert.Equal(
            "Microsoft SQL Server 2019 (RTM-CU18) (KB5017593) - 15.0.4261.1 (X64) · Windows Server 2019 Standard 10.0",
            line);
    }

    [Fact]
    public void ServerVersion_falls_back_to_the_build_when_the_host_cannot_be_found()
    {
        // A localised install says "on" in its own language. Naming the build and staying quiet about the
        // host beats printing whatever happens to follow the last "on" in a German sentence.
        Assert.Equal("Microsoft SQL Server 2019", ActivityTables.ServerVersion("Microsoft SQL Server 2019\n\tsomething"));
        Assert.Equal(string.Empty, ActivityTables.ServerVersion(string.Empty));
    }

    private static ProcessRow Process(int sessionId, int blockedBy) => new(
        sessionId, true, "sa", "Sales", "running", "SELECT", "app", 0, "", "", blockedBy, 0, "host", "default");

    // What ActivitySampler does after reading the Processes result: the blocking chain is a property of the
    // whole set, not of any one row.
    private static IReadOnlyList<ProcessRow> WithHeadBlockers(IReadOnlyList<ProcessRow> rows)
    {
        var heads = ActivityRates.HeadBlockers(rows.Select(p => (p.SessionId, p.BlockedBy)));
        return [.. rows.Select(p => p with { HeadBlocker = heads.Contains(p.SessionId) })];
    }

    private static QueryTotals Query(long executions, long workerTimeUs, long elapsedUs) =>
        new("key", "SELECT 1", "Sales", executions, workerTimeUs, 0, 0, 0, elapsedUs, 1);

    private static ActivitySample Sample(
        DateTimeOffset takenAt,
        IReadOnlyList<ProcessRow>? processes = null,
        IReadOnlyList<WaitTotals>? waits = null,
        IReadOnlyList<FileIoTotals>? files = null,
        IReadOnlyList<QueryTotals>? queries = null) =>
        new(takenAt, processes ?? [], waits ?? [], files ?? [], queries ?? [], [], new ServerCounters(0, 0, 0, 0, 0), "");
}
