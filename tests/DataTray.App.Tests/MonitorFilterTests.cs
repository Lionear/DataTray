using DataTray.App.ViewModels;
using DataTray.Sdk.Query;

namespace DataTray.App.Tests;

/// <summary>Covers the Activity Monitor's Database / "blocking only" filters. The filter is a pure static
/// on the view-model, so these run without any DI, Avalonia, or a live server.</summary>
public class MonitorFilterTests
{
    // session_id, database, blocking_session_id — 51 blocks 52 (both in Sales), 60 is idle in Ops.
    private static QueryResult Sessions() => new()
    {
        Columns =
        [
            new ResultColumn("session_id", typeof(int)),
            new ResultColumn("database", typeof(string)),
            new ResultColumn("blocking_session_id", typeof(int))
        ],
        Rows =
        [
            [51, "Sales", 0],
            [52, "Sales", 51],
            [60, "Ops", null]
        ]
    };

    private static IReadOnlyList<object?[]> Filter(string? database, bool blockingOnly) =>
        DocumentViewModel.FilterSessionRows(
            Sessions(), "database", "blocking_session_id", "session_id", database, blockingOnly);

    [Fact]
    public void NoFilters_KeepsEveryRow() => Assert.Equal(3, Filter(null, false).Count);

    [Fact]
    public void Database_KeepsOnlyThatDatabase()
    {
        var rows = Filter("Sales", false);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("Sales", row[1]));
    }

    [Fact]
    public void Database_IsCaseInsensitive() => Assert.Single(Filter("ops", false));

    [Fact]
    public void BlockingOnly_KeepsBlockedRowsAndTheirBlockers()
    {
        var ids = Filter(null, true).Select(row => row[0]).ToList();
        Assert.Equal([51, 52], ids);
    }

    [Fact]
    public void BlockingOnly_DropsIdleAndUnblockedRows() =>
        Assert.DoesNotContain(Filter(null, true), row => Equals(row[0], 60));

    [Fact]
    public void BothFilters_Combine() => Assert.Equal(2, Filter("Sales", true).Count);

    [Fact]
    public void UnknownColumns_AreANoOp() =>
        Assert.Equal(3, DocumentViewModel
            .FilterSessionRows(Sessions(), string.Empty, string.Empty, string.Empty, "Sales", true).Count);
}
