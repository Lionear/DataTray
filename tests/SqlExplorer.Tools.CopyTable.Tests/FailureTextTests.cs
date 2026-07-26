using SqlExplorer.Tools.CopyTable;

namespace SqlExplorer.Tools.CopyTable.Tests;

public class FailureTextTests
{
    private static string Summarise(int kinds, int lines) => $"…and {kinds} more kind(s), {lines} line(s).";

    private static string? Run(string? message) => FailureText.Consolidate(message, Summarise);

    [Fact]
    public void Repeated_lines_collapse_to_one_with_a_count()
    {
        // What a batched insert against SQL Server actually hands back: one complaint per offending row.
        var complaint = "String or binary data would be truncated in table 'db.dbo.AuditLogs', column 'Event'.";
        var message = string.Join("\n", Enumerable.Repeat(complaint, 412));

        Assert.Equal($"{complaint}  (×412)", Run(message));
    }

    [Fact]
    public void Distinct_lines_all_survive_in_first_seen_order()
    {
        var message = "second thing\nfirst thing\nsecond thing";

        Assert.Equal("second thing  (×2)\nfirst thing", Run(message));
    }

    [Fact]
    public void A_single_line_message_is_left_alone()
    {
        Assert.Equal("could not connect", Run("could not connect"));
    }

    [Fact]
    public void Blank_lines_are_dropped_and_lines_are_trimmed()
    {
        Assert.Equal("a\nb", Run("  a  \n\n\n b \n"));
    }

    [Fact]
    public void Past_the_cap_the_rest_is_summarised_rather_than_dropped()
    {
        // An error list that was truncated but looks complete is worse than a long one.
        var lines = Enumerable.Range(1, FailureText.MaxDistinctLines + 3).Select(i => $"error {i}");
        var message = string.Join("\n", lines.Concat(["error 1", "error 1"]));

        var result = Run(message)!;
        var rendered = result.Split('\n');

        Assert.Equal(FailureText.MaxDistinctLines + 1, rendered.Length);
        Assert.Equal("error 1  (×3)", rendered[0]);
        Assert.Equal("…and 3 more kind(s), 3 line(s).", rendered[^1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_message_comes_back_unchanged(string? message) => Assert.Equal(message, Run(message));
}
