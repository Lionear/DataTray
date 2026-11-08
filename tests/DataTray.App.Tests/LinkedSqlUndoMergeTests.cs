using DataTray.App.Views;

namespace DataTray.App.Tests;

public class LinkedSqlUndoMergeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(499, true)]
    [InlineData(500, false)]
    [InlineData(null, false)] // first linked write
    public void ContinuesGroup_OnlyWithinFiveHundredMilliseconds(int? msSinceLastWrite, bool expected)
    {
        DateTimeOffset? lastWrite = Now - msSinceLastWrite * TimeSpan.FromMilliseconds(1); // null stays null

        Assert.Equal(expected, LinkedSqlUndoMerge.ContinuesGroup(lastWrite, Now));
    }
}
