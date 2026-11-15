using DataTray.App.ViewModels;
using DataTray.Sdk.Query;

namespace DataTray.App.Tests;

public class QueryMessageLogTests
{
    [Fact] // SE-300: messages arrive on the driver's thread; the UI sees them only when it flushes, in arrival order.
    public void Report_ShowsMessagesAfterFlushInArrivalOrderWithTheirTime()
    {
        var clock = new FakeClock();
        var log = new QueryMessageLog(clock);

        log.Report(new QueryMessage(QueryMessageKind.Info, "Region 1  deleted total: 20000"));
        clock.Advance(TimeSpan.FromMilliseconds(250));
        log.Report(new QueryMessage(QueryMessageKind.RowsAffected, "(20000 rows affected)"));
        log.Report(new QueryMessage(QueryMessageKind.Warning, "Warning: Null value is eliminated"));
        log.Report(new QueryMessage(QueryMessageKind.Error, "conflict") { Header = "Msg 547, Level 16, State 0, Line 7", Code = "Msg 547" });
        log.Report(new QueryMessage(QueryMessageKind.Error, "second") { Code = "Msg 3621" });
        log.Report(new QueryMessage(QueryMessageKind.RowsAffected, "(1 row affected)"));
        Assert.Empty(log.Visible);

        log.Flush();

        Assert.Equal(["Region 1  deleted total: 20000", "(20000 rows affected)", "Warning: Null value is eliminated", "conflict",
                "second", "(1 row affected)"],
            log.Visible.Select(e => e.Text));
        Assert.Equal(clock.GetLocalNow().AddMilliseconds(-250), log.Visible[0].Time);
        Assert.Equal((6, 3, 1, 2), (log.Total, log.InfoCount, log.WarningCount, log.ErrorCount));
        Assert.Equal("Msg 547", log.FirstErrorCode);
        Assert.Equal("second", log.LastText);
    }

    [Fact] // SE-300: thousands of lines — the log keeps the latest N and says how many it dropped.
    public void Flush_PastCapacity_KeepsTheLatestAndStillCountsEverything()
    {
        var log = new QueryMessageLog(new FakeClock(), capacity: 3);

        for (var i = 1; i <= 4; i++)
        {
            log.Report(new QueryMessage(QueryMessageKind.Info, $"m{i}"));
        }

        log.Flush();
        log.Report(new QueryMessage(QueryMessageKind.Info, "m5"));
        log.Flush();

        Assert.Equal(["m3", "m4", "m5"], log.Visible.Select(e => e.Text));
        Assert.Equal((5, 2), (log.Total, log.Dropped));
    }

    [Fact] // SE-300: the driver raises its events on its own threads, often several at once — none may get lost.
    public async Task Report_FromManyThreadsAtOnce_LosesNothing()
    {
        var log = new QueryMessageLog(new FakeClock(), capacity: 100_000);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 2000; i++)
            {
                log.Report(new QueryMessage(QueryMessageKind.Info, $"{t}:{i}"));
            }
        })));
        log.Flush();

        Assert.Equal(16_000, log.Visible.Count);
        Assert.Equal(16_000, log.Total);
    }

    public static IEnumerable<object[]> Filters() =>
    [
        ["", true, true, true, new[] { "Region 1 done", "(5 rows affected)", "Warning: slow", "Region 2 failed" }],
        ["region", true, true, true, new[] { "Region 1 done", "Region 2 failed" }],
        ["", false, true, true, new[] { "Warning: slow", "Region 2 failed" }],
        ["", true, false, false, new[] { "Region 1 done", "(5 rows affected)" }],
        ["REGION", true, true, false, new[] { "Region 1 done" }],
    ];

    [Theory, MemberData(nameof(Filters))] // SE-300: one search box plus three level toggles; hidden, not deleted.
    public void Filter_ShowsOnlyMatchingMessages(string text, bool info, bool warnings, bool errors, string[] expected)
    {
        var log = new QueryMessageLog(new FakeClock());
        log.Report(new QueryMessage(QueryMessageKind.Info, "Region 1 done"));
        log.Report(new QueryMessage(QueryMessageKind.RowsAffected, "(5 rows affected)"));
        log.Report(new QueryMessage(QueryMessageKind.Warning, "Warning: slow"));
        log.Report(new QueryMessage(QueryMessageKind.Error, "Region 2 failed"));
        log.Flush();

        log.FilterText = text;
        log.ShowInfo = info;
        log.ShowWarnings = warnings;
        log.ShowErrors = errors;

        Assert.Equal(expected, log.Visible.Select(e => e.Text));
        Assert.Equal(4, log.Total);
    }

    [Fact] // SE-300: Copy takes what is shown, as "[time] text", with an error's server header in front.
    public void CopyText_IsTheShownLinesWithTheirTime()
    {
        var clock = new FakeClock();
        var log = new QueryMessageLog(clock);
        log.Report(new QueryMessage(QueryMessageKind.Info, "before"));
        log.Report(new QueryMessage(QueryMessageKind.Error, "conflict") { Header = "Msg 547, Level 16, State 0, Line 7" });
        log.Flush();

        var time = clock.GetLocalNow().ToString("HH:mm:ss.fff");
        Assert.Equal($"[{time}] before\n[{time}] Msg 547, Level 16, State 0, Line 7\n[{time}] conflict",
            log.CopyText(log.Visible).ReplaceLineEndings("\n"));
    }

    [Fact] // SE-300: a client line (the cancel note) counts as a message and lands after everything already queued.
    public void AddClientLine_LandsAfterQueuedServerMessages()
    {
        var log = new QueryMessageLog(new FakeClock());
        log.Report(new QueryMessage(QueryMessageKind.Info, "batch 1"));

        log.AddClientLine("Query cancelled after 00:42.");
        log.Flush();

        Assert.Equal(["batch 1", "Query cancelled after 00:42."], log.Visible.Select(e => e.Text));
        Assert.True(log.Visible[1].IsClient);
        Assert.Equal(2, log.Total);
    }

    [Fact] // SE-300: scrolling up pauses following; the pill counts what arrived since, and resuming resets it.
    public void Flush_WhilePaused_CountsNewMessagesUntilFollowingResumes()
    {
        var log = new QueryMessageLog(new FakeClock());
        log.Report(new QueryMessage(QueryMessageKind.Info, "seen"));
        log.Flush();

        log.IsFollowing = false;
        log.Report(new QueryMessage(QueryMessageKind.Info, "new 1"));
        log.Report(new QueryMessage(QueryMessageKind.RowsAffected, "(1 row affected)"));
        log.Flush();
        Assert.Equal(2, log.NewWhilePaused);

        log.IsFollowing = true;
        Assert.Equal(0, log.NewWhilePaused);
    }
}
