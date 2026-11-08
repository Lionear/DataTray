namespace DataTray.App.Views;

/// <summary>Whether a pane's write to the linked editor joins the previous undo step, so one Ctrl+Z undoes a burst.</summary>
internal static class LinkedSqlUndoMerge
{
    public static readonly TimeSpan Window = TimeSpan.FromMilliseconds(500);

    public static bool ContinuesGroup(DateTimeOffset? lastWrite, DateTimeOffset now) =>
        lastWrite is { } last && now - last < Window;
}
