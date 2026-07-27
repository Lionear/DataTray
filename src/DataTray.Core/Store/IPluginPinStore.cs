namespace DataTray.Core.Store;

/// <summary>
/// Persists per-plugin **version pins** (<c>plugin-pins.json</c>). A pinned plugin id is held at the
/// requested version — <see cref="PluginUpdateService.DetectUpdates"/> skips it, so "Update all" leaves
/// it alone until the pin is cleared. Absent = auto (highest compatible; default behaviour). Written
/// atomically like the other JSON stores.
/// </summary>
public interface IPluginPinStore
{
    /// <summary>Current pin for a plugin id, or null when it isn't pinned (auto = latest compatible).</summary>
    string? GetPin(string pluginId);

    /// <summary>Every current pin — used to drive UI badges without a full read per row.</summary>
    IReadOnlyDictionary<string, string> GetAll();

    /// <summary>Pin a plugin to an exact version string. Overwrites any previous pin.</summary>
    void Pin(string pluginId, string version);

    /// <summary>Clear the pin for a plugin id (no-op when absent).</summary>
    void Unpin(string pluginId);
}
