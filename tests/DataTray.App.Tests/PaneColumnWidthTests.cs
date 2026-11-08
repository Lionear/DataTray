using DataTray.App.Views;

namespace DataTray.App.Tests;

public class PaneColumnWidthTests
{
    [Theory]
    [InlineData(360, 1000, 360)]   // fits
    [InlineData(900, 1000, 752)]   // 1000 - 8 splitter - 240 editor
    [InlineData(100, 1000, 280)]   // pane minimum
    [InlineData(360, 400, 280)]    // row too narrow for both: the pane minimum wins
    public void Clamp_KeepsPaneAndEditorMinimums(double desired, double rowWidth, double expected)
    {
        Assert.Equal(expected, PaneColumnWidth.Clamp(desired, rowWidth));
    }
}
