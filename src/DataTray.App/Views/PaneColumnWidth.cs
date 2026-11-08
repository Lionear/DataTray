namespace DataTray.App.Views;

/// <summary>The pane column's width limits: the pane keeps its minimum, the editor beside it keeps its own.</summary>
internal static class PaneColumnWidth
{
    public const double PaneMin = 280;
    public const double EditorMin = 240;
    public const double SplitterTotal = 8; // 4 px splitter plus its 2 px margin either side

    public static double Clamp(double desired, double rowWidth) =>
        Math.Max(PaneMin, Math.Min(desired, rowWidth - SplitterTotal - EditorMin));
}
