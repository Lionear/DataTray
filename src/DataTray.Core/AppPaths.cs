namespace DataTray.Core;

/// <summary>
/// The per-user data root, in one place. Every store used to build this path itself — sixteen copies of
/// <c>Path.Combine(ApplicationData, "Lionear", "SqlExplorer")</c> — which meant the rename in SE-206 would
/// have been sixteen chances to miss one, and a missed one silently writes to a folder nothing else reads.
/// </summary>
/// <remarks>
/// On Windows <see cref="Environment.SpecialFolder.ApplicationData"/> is %APPDATA%; on Unix it is
/// $XDG_CONFIG_HOME (or ~/.config), so the root is ~/.config/Lionear/DataTray there.
/// </remarks>
public static class AppPaths
{
    private const string Vendor = "Lionear";
    private const string Product = "DataTray";

    /// <summary>The folder used before the DataTray rename (SE-202). Read only by the migration.</summary>
    private const string LegacyProduct = "SqlExplorer";

    private static string AppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>Writable per-user root: settings, connections, history, plugins and plugin data.</summary>
    public static string Root => Path.Combine(AppData, Vendor, Product);

    /// <summary>The pre-rename root. Exists only so the migration can find and copy it.</summary>
    public static string LegacyRoot => Path.Combine(AppData, Vendor, LegacyProduct);

    /// <summary>A file directly under <see cref="Root"/>, e.g. <c>connections.json</c>.</summary>
    public static string File(string name) => Path.Combine(Root, name);

    /// <summary>A subfolder of <see cref="Root"/>, e.g. <c>plugins</c> or <c>plugin-data</c>.</summary>
    public static string Dir(string name) => Path.Combine(Root, name);
}
