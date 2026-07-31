using DataTray.Sdk.Localization;
using DataTray.Sdk.Viewers;

namespace DataTray.App.Viewers;

/// <summary>
/// The host's <see cref="IViewerContext"/> for one query tab. The tab owns it and pushes updates in;
/// the viewer's control only reads and subscribes. Built once per selected viewer and kept alive across
/// refreshes, so a viewer that tracks its own state (scroll position, expanded nodes) keeps it when the
/// page turns.
/// </summary>
public sealed class DocumentViewerContext(ResultView result, IPluginLocalizer localizer) : IViewerContext
{
    public ResultView Result { get; private set; } = result;

    public int? SelectedRowIndex { get; private set; }

    public int? SelectedColumnIndex { get; private set; }

    public IPluginLocalizer Localizer { get; } = localizer;

    public event EventHandler? DataChanged;

    public event EventHandler? SelectionChanged;

    /// <summary>Swap in a fresh snapshot and tell the viewer. Called on the UI thread — the property is
    /// replaced before the event fires, so a handler reads the new value straight off <see cref="Result"/>.</summary>
    public void Update(ResultView result)
    {
        Result = result;
        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Move the reported selection and tell the viewer. No-ops when nothing actually moved, so a
    /// grid that re-raises its selection on every refresh doesn't cost a re-render.</summary>
    public void SetSelection(int? rowIndex, int? columnIndex)
    {
        if (SelectedRowIndex == rowIndex && SelectedColumnIndex == columnIndex)
        {
            return;
        }

        SelectedRowIndex = rowIndex;
        SelectedColumnIndex = columnIndex;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
}
