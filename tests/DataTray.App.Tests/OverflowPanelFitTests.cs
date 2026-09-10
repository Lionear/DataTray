using DataTray.App.Controls;

namespace DataTray.App.Tests;

/// <summary>
/// The measure pass's decision, tested without a display (SE-255 §4). Everything else in the panel is
/// arranging rectangles; this is the part that can be wrong in a way nobody sees until a window is
/// dragged narrow.
/// </summary>
public class OverflowPanelFitTests
{
    private const double Spacing = 8;

    private static bool[] Fit(double available, double[] widths, bool[]? pinned = null) =>
        OverflowPanel.Fit(widths, pinned ?? new bool[widths.Length], Spacing, available);

    [Fact]
    public void Everything_fits_when_there_is_room()
    {
        Assert.Equal([true, true, true], Fit(500, [100, 100, 100]));
    }

    [Fact]
    public void Exact_fit_counts_as_fitting()
    {
        // 100 + 8 + 100 + 8 + 100 = 316: the boundary is inclusive, so a strip is never overflowed by a
        // rounding hair.
        Assert.Equal([true, true, true], Fit(316, [100, 100, 100]));
    }

    [Fact]
    public void The_tail_overflows_first()
    {
        // The user's order is the priority order: what they put first survives longest.
        Assert.Equal([true, true, false], Fit(240, [100, 100, 100]));
    }

    [Fact]
    public void Pinned_children_come_off_the_budget_and_never_overflow()
    {
        // The pinned child (200 wide) leaves room for one of the two 100s.
        Assert.Equal([true, false, true], Fit(320, [100, 100, 200], [false, false, true]));
    }

    [Fact]
    public void Pinned_children_survive_a_window_too_narrow_for_anything_else()
    {
        Assert.Equal([false, false, true], Fit(80, [100, 100, 200], [false, false, true]));
    }

    [Fact]
    public void Nothing_fits_but_the_strip_still_resolves()
    {
        Assert.Equal([false, false], Fit(10, [100, 100]));
    }

    [Fact]
    public void An_infinite_width_fits_everything()
    {
        // Measured inside a scroll viewer or during a desired-size probe: never decide overflow there.
        Assert.Equal([true, true], Fit(double.PositiveInfinity, [100, 100]));
    }

    [Fact]
    public void The_decision_is_stable_when_fed_its_own_result()
    {
        // The oscillation guard, stated as a property: re-deciding from the same widths at the same width
        // yields the same answer, because the panel never measures the post-collapse state.
        var first = Fit(240, [100, 100, 100]);
        var second = Fit(240, [100, 100, 100]);

        Assert.Equal(first, second);
    }

    [Fact]
    public void An_empty_bar_is_not_an_error()
    {
        Assert.Empty(Fit(100, []));
    }
}
