using DataTray.Core.Export;
using DataTray.Sdk.Query;

namespace DataTray.Core.Tests.Export;

/// <summary>
/// The styled variants exist to survive Word's rendering engine, which ignores a stylesheet — so what these
/// assert is that the styling is inline and per row, not that it looks nice.
/// </summary>
public class ResultExporterHtmlTests
{
    private static readonly ResultColumn[] Columns =
    [
        new("Database", typeof(string)),
        new("Size", typeof(int)),
    ];

    private static readonly object?[][] Rows =
    [
        ["Donations", 4812],
        ["Staging", null],
        ["Members", 1204],
    ];

    [Fact]
    public void Plain_stays_the_bare_table_it_always_was()
    {
        var html = ResultExporter.ToHtml(Columns, Rows);

        Assert.StartsWith("<table>", html);
        Assert.DoesNotContain("style=", html);
        // A null cell stays blank here — spelling NULL out is a styled-table affordance.
        Assert.Contains("<td>Staging</td><td></td>", html);
    }

    [Theory]
    [InlineData(HtmlTableStyle.Hairlines)]
    [InlineData(HtmlTableStyle.HeaderFill)]
    [InlineData(HtmlTableStyle.HeaderFillZebra)]
    public void Styled_tables_carry_their_css_inline(HtmlTableStyle style)
    {
        var html = ResultExporter.ToHtml(Columns, Rows, style);

        Assert.DoesNotContain("<style", html);
        Assert.DoesNotContain("class=", html);
        Assert.Contains("<table style=\"border-collapse:collapse;", html);
        // Every cell states its own alignment: the numeric column right, the text column left.
        Assert.Contains("text-align:right", html);
        Assert.Contains("text-align:left", html);
        // A null renders as a visible, distinguishable NULL rather than an empty cell.
        Assert.Contains("font-style:italic;\">NULL</td>", html);
    }

    [Fact]
    public void Zebra_tints_every_other_row_and_only_zebra_does()
    {
        var zebra = ResultExporter.ToHtml(Columns, Rows, HtmlTableStyle.HeaderFillZebra);
        var flat = ResultExporter.ToHtml(Columns, Rows, HtmlTableStyle.HeaderFill);

        // Three rows: the middle one tinted, the outer two left on paper white. Word has no nth-child, so
        // this has to be true of the markup itself.
        Assert.Equal(2, Occurrences(zebra, "background:#F2F6FD"));
        Assert.Equal(4, Occurrences(zebra, "background:#FFFFFF"));
        Assert.Equal(0, Occurrences(flat, "background:#F2F6FD"));
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
