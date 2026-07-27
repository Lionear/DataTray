namespace DataTray.Core.Store;

/// <summary>The stage an install is in, for progress reporting.</summary>
public enum InstallPhase
{
    Downloading,
    Verifying,
    Extracting,
    Staging,
    Done
}

/// <summary>A progress tick during an install. <see cref="TotalBytes"/> is null until known.</summary>
public sealed record InstallProgress(string PluginId, InstallPhase Phase, long BytesDownloaded, long? TotalBytes);

/// <summary>Outcome of a single install/update/rollback. On success the change is staged and applies on
/// the next restart (<see cref="RestartRequired"/> is then true).</summary>
public sealed record InstallOutcome(string PluginId, string? Version, bool Success, string? Error, bool RestartRequired)
{
    public static InstallOutcome Ok(string id, string? version) => new(id, version, true, null, true);

    public static InstallOutcome Fail(string id, string error) => new(id, null, false, error, false);
}

/// <summary>
/// Downloads, verifies and stages plugins into the per-user folder. Nothing is loaded live — an install
/// stages files and marks the plugin pending, and <c>PluginMaintenance</c> applies it (swap/promote) at
/// the next startup. Update and downgrade are just <see cref="InstallAsync"/> with a chosen version; the
/// pipeline stages a replacement (<c>&lt;id&gt;.next</c>) beside the running copy and keeps one backup
/// (<c>&lt;id&gt;.prev</c>) for <see cref="RequestRollback"/>.
/// </summary>
public interface IPluginInstaller
{
    /// <summary>
    /// Install (or update/downgrade to) a specific version from a store: download with a size cap,
    /// verify SHA-256 against the index, gate on host-API compatibility, unzip zip-slip-safe, then stage.
    /// </summary>
    Task<InstallOutcome> InstallAsync(StoreEntry entry, StoreVersion version, IProgress<InstallProgress>? progress, CancellationToken ct);

    /// <summary>Same pipeline as <see cref="InstallAsync"/> but from a local <c>.zip</c> (no download, no
    /// index checksum — the plugin's own manifest is validated and the host-API gate still applies).</summary>
    Task<InstallOutcome> InstallFromFileAsync(string zipPath, IProgress<InstallProgress>? progress, CancellationToken ct);

    /// <summary>Stage a one-step rollback to the kept previous version — instant and offline (no
    /// re-download). Applies on the next restart. Fails if there is no <c>&lt;id&gt;.prev</c> backup.</summary>
    InstallOutcome RequestRollback(string pluginId);
}
