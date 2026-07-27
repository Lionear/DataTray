namespace DataTray.Core.Plugins;

/// <summary>
/// The two roots plugins live in. <see cref="BundledRoot"/> ships beside the executable and is
/// read-only (the MVP providers, staged at build time); <see cref="UserRoot"/> is the per-user,
/// writable folder the Plugin Store installs into — no admin rights needed. When the same plugin id
/// exists in both, the user copy wins (see <see cref="PluginDiscovery"/>).
/// </summary>
public static class PluginPaths
{
    /// <summary>Read-only plugins staged next to the app binary at build time.</summary>
    public static string BundledRoot => Path.Combine(AppContext.BaseDirectory, "plugins");

    /// <summary>Writable per-user plugin folder (Store installs land here, one subfolder per id).</summary>
    public static string UserRoot => AppPaths.Dir("plugins");

    /// <summary>The per-user install folder for a single plugin id.</summary>
    public static string UserPluginDir(string id) => Path.Combine(UserRoot, id);
}
