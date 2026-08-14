using DataTray.Tools.MsSqlAdmin;

namespace DataTray.Tools.MsSqlAdmin.Tests;

/// <summary>
/// The Activity Monitor's arithmetic. Every figure it shows that is not a plain DMV column comes from here,
/// and a wrong rate is the one kind of error a database cannot contradict — the grid would simply lie
/// convincingly.
/// </summary>
public class ActivityRatesTests
{
    [Fact]
    public void PerSecond_divides_the_change_by_the_elapsed_time()
    {
        Assert.Equal(20, ActivityRates.PerSecond(now: 300, before: 100, seconds: 10));
    }

    [Fact]
    public void PerSecond_reports_nothing_on_the_first_sample()
    {
        // No previous sample means no elapsed time, and a rate needs two points.
        Assert.Equal(0, ActivityRates.PerSecond(now: 500, before: 0, seconds: 0));
    }

    [Fact]
    public void PerSecond_reports_nothing_when_the_counter_was_reset()
    {
        // A restarted instance, a cleared wait-stats DMV or an evicted plan sends a cumulative counter
        // backwards; the honest answer is "no rate", not a large negative one.
        Assert.Equal(0, ActivityRates.PerSecond(now: 40, before: 5_000, seconds: 10));
    }

    [Fact]
    public void HeadBlockers_marks_the_session_at_the_head_of_a_chain()
    {
        // 51 blocks 52, which blocks 53. Only 51 is the head blocker.
        var heads = ActivityRates.HeadBlockers([(51, 0), (52, 51), (53, 52)]);

        Assert.Equal([51], heads);
    }

    [Fact]
    public void HeadBlockers_ignores_a_session_blocking_itself()
    {
        // A parallel query waiting on its own exchange reports itself as its blocker; that must not stop it
        // being recognised as the head of the chain it is actually blocking.
        var heads = ActivityRates.HeadBlockers([(60, 60), (61, 60)]);

        Assert.Equal([60], heads);
    }

    [Fact]
    public void HeadBlockers_is_empty_when_nothing_is_blocked()
    {
        Assert.Empty(ActivityRates.HeadBlockers([(51, 0), (52, 0)]));
    }

    [Theory]
    [InlineData("PAGEIOLATCH_SH", "Buffer I/O")]
    [InlineData("PAGELATCH_EX", "Buffer Latch")]
    [InlineData("LCK_M_X", "Lock")]
    [InlineData("LATCH_EX", "Latch")]
    [InlineData("WRITELOG", "Logging")]
    [InlineData("RESOURCE_SEMAPHORE", "Memory")]
    [InlineData("RESOURCE_SEMAPHORE_QUERY_COMPILE", "Compilation")]
    [InlineData("ASYNC_NETWORK_IO", "Network I/O")]
    [InlineData("BACKUPIO", "Backup")]
    [InlineData("SOME_FUTURE_WAIT", "Other")]
    public void WaitCategory_buckets_wait_types_the_way_SSMS_does(string waitType, string expected)
    {
        Assert.Equal(expected, ActivityRates.WaitCategory(waitType));
    }

    [Fact]
    public void WaitCategory_keeps_page_io_latches_out_of_the_buffer_latch_bucket()
    {
        // The prefixes overlap: PAGEIOLATCH_* is a page being fetched from disk, PAGELATCH_* is a latch on a
        // page already in memory. Getting this backwards would blame the disks for a memory contention.
        Assert.NotEqual(ActivityRates.WaitCategory("PAGEIOLATCH_EX"), ActivityRates.WaitCategory("PAGELATCH_EX"));
    }

    [Fact]
    public void CompareCells_orders_numbers_by_value_not_by_their_digits()
    {
        // Built through the formatter the grid itself uses, so the thousands separator is whatever the
        // running culture writes — which is the separator the sort has to cope with.
        var nine = ActivityRates.Number(9);
        var ten = ActivityRates.Number(10);
        var thousands = ActivityRates.Number(1234);

        Assert.True(ActivityRates.CompareCells(nine, ten) < 0);
        Assert.True(ActivityRates.CompareCells(ten, thousands) < 0);
        // As text, "1,234" sorts before "9" — the whole complaint behind SE-265's numeric half.
        Assert.True(ActivityRates.CompareCells(thousands, nine) > 0);
    }

    [Fact]
    public void CompareCells_reads_the_number_in_front_of_a_unit()
    {
        // Average Duration is written "200 ms". Sorted as text, 1,234 ms lands between 1 ms and 2 ms.
        Assert.True(ActivityRates.CompareCells(ActivityRates.Milliseconds(2), ActivityRates.Milliseconds(1234)) < 0);
        Assert.True(ActivityRates.CompareCells(ActivityRates.Milliseconds(1234), ActivityRates.Milliseconds(2)) > 0);
    }

    [Fact]
    public void CompareCells_is_a_total_order_on_a_column_that_mixes_numbers_and_text()
    {
        // "Blocked By" is empty or a session id; a wait resource is a page address. Comparing each pair by
        // whichever rule fits it is not transitive — 9 < 10 as numbers, "1x" between them as text — and
        // List.Sort is entitled to throw on a comparer that contradicts itself, which would look to a user
        // exactly like a header click that does nothing.
        List<string> cells = ["9", "1x", "10", "", "2"];

        cells.Sort(ActivityRates.CompareCells);

        Assert.Equal(["2", "9", "10", "", "1x"], cells);
    }

    [Fact]
    public void IsBenignWait_drops_the_idle_background_waits()
    {
        // These dwarf every real wait — a server idle for a week has a week of LAZYWRITER_SLEEP — so the
        // Resource Waits grid would otherwise report sleeping as the server's main activity.
        Assert.True(ActivityRates.IsBenignWait("LAZYWRITER_SLEEP"));
        Assert.True(ActivityRates.IsBenignWait("sleep_task"));
        Assert.False(ActivityRates.IsBenignWait("LCK_M_X"));
    }
}
