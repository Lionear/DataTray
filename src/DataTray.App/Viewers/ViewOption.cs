using DataTray.Sdk.Viewers;

namespace DataTray.App.Viewers;

/// <summary>
/// One entry in the "View" switcher above the result grid. <see cref="Plugin"/> null is the built-in grid —
/// always present, always first, and the fallback when the selected viewer stops applying.
/// </summary>
/// <param name="Id">Stable id; <see cref="GridId"/> for the built-in grid, otherwise the viewer's own.</param>
/// <param name="Label">Already-localized text for the button.</param>
/// <param name="Plugin">The viewer to render, or null for the built-in grid.</param>
public sealed record ViewOption(string Id, string Label, IViewerPlugin? Plugin)
{
    /// <summary>Id of the built-in grid entry.</summary>
    public const string GridId = "grid";

    public bool IsGrid => Plugin is null;
}
