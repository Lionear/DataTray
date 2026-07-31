using DataTray.Sdk.Localization;

namespace DataTray.Sdk.Viewers;

/// <summary>
/// What the host hands a viewer's control: the current snapshot, where the selection sits, and the two
/// events that let one long-lived control follow the tab instead of being rebuilt. A viewer created once
/// per result set keeps its own state (scroll position, expanded nodes) across a browse page turn or a
/// monitor refresh — which is the point of <see cref="DataChanged"/> rather than a fresh
/// <see cref="IViewerPlugin.CreateView"/> per update.
/// </summary>
public interface IViewerContext
{
    /// <summary>The current snapshot. Replaced before <see cref="DataChanged"/> is raised, so a handler
    /// reads the new value straight off this property.</summary>
    ResultView Result { get; }

    /// <summary>Row index selected in the grid, or null when nothing is selected.</summary>
    int? SelectedRowIndex { get; }

    /// <summary>Column index selected in the grid, or null when nothing is selected.</summary>
    int? SelectedColumnIndex { get; }

    /// <summary>The plugin's own localizer, or an empty one when it ships no translations — never null.</summary>
    IPluginLocalizer Localizer { get; }

    /// <summary>Raised on the UI thread after <see cref="Result"/> has been replaced — a page turn, a
    /// monitor refresh, a re-run of the query.</summary>
    event EventHandler? DataChanged;

    /// <summary>Raised on the UI thread after the grid selection moved.</summary>
    event EventHandler? SelectionChanged;
}
