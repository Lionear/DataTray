using Avalonia.Media;
using DataTray.Core.Toolbar;

namespace DataTray.App.ViewModels;

/// <summary>
/// The glyph for a toolbar catalog entry. Lives on the App side because <c>DataTray.Core.Toolbar</c> is
/// UI-agnostic — it addresses actions by id and knows nothing about geometry — and is shared by the strip
/// and the Settings ▸ Toolbar list so a row and its button can never drift apart.
/// </summary>
internal static class ToolbarIcons
{
    public static Geometry? For(ToolbarActionEntry entry) => entry.Id switch
    {
        ToolbarCatalog.Ids.NewQueryTab => NodeIcons.Plus,
        ToolbarCatalog.Ids.GoToObject => NodeIcons.Search,
        _ => null,
    };
}
