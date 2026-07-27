namespace DataTray.Core.Plugins;

/// <summary>One plugin folder found on disk: its manifest (or the reason it couldn't be read) and
/// which root it came from. This is the pre-load view — no assembly has been loaded yet.</summary>
public sealed record DiscoveredPlugin(
    string Directory,
    PluginOrigin Origin,
    PluginManifest? Manifest,
    string? ManifestError)
{
    /// <summary>The manifest id when it parsed; used to dedup a user copy over a bundled one.</summary>
    public string? Id => Manifest?.Id;
}

/// <summary>
/// Scans the bundled and per-user plugin roots and returns one entry per plugin, with the user copy
/// winning when the same id exists in both. A folder whose <c>plugin.json</c> is missing or unreadable
/// is dropped (bundled) or surfaced with a <see cref="DiscoveredPlugin.ManifestError"/> (user) rather
/// than crashing discovery — the catalog turns that into an "installed but not loadable" row.
/// </summary>
public static class PluginDiscovery
{
    /// <summary>
    /// Discover across both roots. Bundled is scanned first, then user; a user plugin with the same
    /// manifest id replaces the bundled one. Folders without a parseable manifest keep their spot only
    /// when user-installed (so the Store can explain why they failed); duplicate ids are resolved on the
    /// parsed id, so an unparseable folder can never mask a good one.
    /// </summary>
    public static IReadOnlyList<DiscoveredPlugin> Discover(string bundledRoot, string userRoot)
    {
        var byId = new Dictionary<string, DiscoveredPlugin>(StringComparer.Ordinal);
        var unidentified = new List<DiscoveredPlugin>();

        void Scan(string root, PluginOrigin origin)
        {
            foreach (var probe in ScanRoot(root, origin))
            {
                if (probe.Id is { } id)
                {
                    // Later root (user) wins on id conflict.
                    byId[id] = probe;
                }
                else if (origin == PluginOrigin.UserInstalled)
                {
                    // Keep unreadable user folders so the Store can report the failure.
                    unidentified.Add(probe);
                }
            }
        }

        Scan(bundledRoot, PluginOrigin.Bundled);
        Scan(userRoot, PluginOrigin.UserInstalled);

        return byId.Values.Concat(unidentified)
            .OrderBy(p => p.Id ?? Path.GetFileName(p.Directory), StringComparer.Ordinal)
            .ToList();
    }

    private static IEnumerable<DiscoveredPlugin> ScanRoot(string root, PluginOrigin origin)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            var manifestPath = Path.Combine(dir, "plugin.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            PluginManifest? manifest = null;
            string? error = null;
            try
            {
                manifest = PluginManifest.Load(manifestPath);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            yield return new DiscoveredPlugin(dir, origin, manifest, error);
        }
    }
}
