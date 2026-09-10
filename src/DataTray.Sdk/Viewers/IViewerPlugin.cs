using Avalonia.Controls;

namespace DataTray.Sdk.Viewers;

/// <summary>
/// An alternative, read-only rendering of a result set — JSON tree, image, later chart or map. The host
/// offers every applicable viewer in the "View" switcher next to the result-set tabs; the built-in grid is
/// always the first entry and the fallback.
/// <para>
/// One assembly may ship several implementations; the loader instantiates them all. This assembly and
/// Avalonia are shared across the plugin ALC boundary, so the returned control has one type identity with
/// the host.
/// </para>
/// </summary>
public interface IViewerPlugin
{
    /// <summary>Stable id, unique across installed viewers. Used to remember the choice and to look up the
    /// shipping plugin's localizer.</summary>
    string Id { get; }

    /// <summary>English fallback label for the switcher, used when <see cref="TitleKey"/> is absent or the
    /// plugin ships no translation for the active language.</summary>
    string Title { get; }

    /// <summary>Localization key for <see cref="Title"/>, resolved against the plugin's own localizer.</summary>
    string? TitleKey => null;

    /// <summary>Optional icon for the switcher entry, as an Avalonia resource/asset key understood by the
    /// shipping plugin. Null means the host draws the label only.</summary>
    string? Icon => null;

    /// <summary>
    /// Whether this viewer can render <paramref name="result"/> at all. Decide on column metadata — the
    /// host calls this on every refresh, so scanning all rows would cost a page turn.
    /// A viewer that returns false is left out of the switcher; if it goes false while selected, the tab
    /// falls back to the grid rather than showing an empty surface.
    /// </summary>
    bool CanView(ResultView result);

    /// <summary>Builds the control. Called once per result set, not per update — follow
    /// <see cref="IViewerContext.DataChanged"/> and <see cref="IViewerContext.SelectionChanged"/> instead.</summary>
    Control CreateView(IViewerContext context);
}
