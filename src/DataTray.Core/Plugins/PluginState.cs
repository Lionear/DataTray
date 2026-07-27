namespace DataTray.Core.Plugins;

/// <summary>Where a plugin was found on disk. Bundled plugins are part of the app and cannot be
/// disabled or uninstalled; user-installed ones (from the Store) can.</summary>
public enum PluginOrigin
{
    Bundled,
    UserInstalled
}

/// <summary>
/// A change staged in <c>plugins-state.json</c> to apply on next startup, because the current
/// non-collectible load contexts can't swap a plugin's files while the process runs (Notes §4.2 —
/// "no real unload"). The Store marks the intent; <see cref="PluginMaintenance"/> applies it before load.
/// </summary>
public enum PluginPendingAction
{
    None,

    /// <summary>Files were unpacked into the user folder; clear the flag and load it next start.</summary>
    Install,

    /// <summary>Delete the user folder next start (safe — nothing is loaded from it yet) and drop the state.</summary>
    Remove
}

/// <summary>Per-plugin persisted state: whether it loads, and any change staged for next startup.</summary>
public sealed record PluginStateEntry
{
    public bool Enabled { get; init; } = true;

    public PluginPendingAction Pending { get; init; } = PluginPendingAction.None;
}

/// <summary>
/// Persists <see cref="PluginStateEntry"/> per plugin id (the Store's enable/disable and pending
/// install/remove markers). Bundled plugins that were never touched have no entry and default to
/// enabled — absence means "on".
/// </summary>
public interface IPluginStateStore
{
    IReadOnlyDictionary<string, PluginStateEntry> GetAll();

    PluginStateEntry Get(string id);

    void Save(string id, PluginStateEntry entry);

    void Remove(string id);
}
