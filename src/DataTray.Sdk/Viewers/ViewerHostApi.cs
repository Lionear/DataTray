namespace DataTray.Sdk.Viewers;

/// <summary>
/// Versioning gate for <c>type: "viewer"</c> plugins, separate from <c>ProviderHostApi</c> and
/// <c>ToolHostApi</c> so the plugin kinds evolve independently. A plugin's <c>plugin.json</c> declares the
/// version it was built for; the loader refuses one this host cannot satisfy.
/// </summary>
public static class ViewerHostApi
{
    // v1 (2026-07-30): the viewer plugin type (SE-75). IViewerPlugin + IViewerContext + ResultView, a
    //                  read-only alternative rendering of the current result set, chosen from the "View"
    //                  switcher on the result-set row.
    public const int Version = 1;

    /// <summary>Oldest plugin ABI this host still loads.</summary>
    public const int MinimumSupported = 1;

    public static bool IsCompatible(int pluginVersion) =>
        pluginVersion >= MinimumSupported && pluginVersion <= Version;
}
