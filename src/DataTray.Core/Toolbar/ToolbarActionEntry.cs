namespace DataTray.Core.Toolbar;

/// <summary>
/// One thing that may sit in the application toolbar. <paramref name="Id"/> is the stable, never-localized
/// key that <c>toolbar.json</c> and the keymap both reference — a host id such as <c>"NewQueryTab"</c>, or
/// <c>"pluginId:localId"</c> for a plugin contribution.
/// </summary>
/// <remarks>
/// <paramref name="Title"/> follows the same split as the shortcut catalog: a host entry carries a resx
/// key (resolved by the App layer, so Core stays UI-agnostic), a plugin entry carries a string the plugin
/// already localized through its own <c>IPluginRuntimeContext.Localizer</c>.
/// </remarks>
public sealed record ToolbarActionEntry(
    string Id,
    string Title,
    ToolbarActionSource Source,
    string? PluginTitle = null);
