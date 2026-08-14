using System.Collections.Generic;
using Avalonia.Media;
using DataTray.Core.Toolbar;

namespace DataTray.App.ViewModels;

/// <summary>
/// The glyph for a toolbar catalog entry. Lives on the App side because <c>DataTray.Core.Toolbar</c> is
/// UI-agnostic — it addresses actions by id and knows nothing about geometry — and is shared by the strip
/// and the Settings ▸ Toolbar list so a row and its button can never drift apart. A plugin's geometry is
/// registered when its contribution is mounted, the same way <see cref="NodeIcons"/> is a static map: it is
/// process-wide and set once at startup.
/// </summary>
internal static class ToolbarIcons
{
    private static readonly Dictionary<string, Geometry?> Plugin = [];

    public static void Register(string id, Geometry? icon) => Plugin[id] = icon;

    public static Geometry? For(ToolbarActionEntry entry) => entry.Id switch
    {
        ToolbarCatalog.Ids.NewQueryTab => NodeIcons.Plus,
        ToolbarCatalog.Ids.GoToObject => NodeIcons.Search,
        _ => Plugin.GetValueOrDefault(entry.Id),
    };
}
