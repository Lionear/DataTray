using DataTray.Sdk;

namespace DataTray.Core.Plugins;

/// <summary>Outcome of scanning one plugin folder — a loaded provider or a reason it was skipped.</summary>
/// <remarks><see cref="Id"/> is the manifest <c>id</c> and is the provider's engine identity.</remarks>
public sealed record ProviderLoadResult(string PluginDirectory, string? Id, IDbProvider? Provider, string? Error)
{
    public bool Succeeded => Provider is not null;
}

/// <summary>
/// Discovers plugins under a root folder (one subfolder per plugin, each with a <c>plugin.json</c>),
/// and loads those of type <see cref="PluginManifest.Types.Provider"/> in their own
/// <see cref="ProviderLoadContext"/>, returning the <see cref="IDbProvider"/> instances. Other plugin
/// types are skipped here — the <c>type</c> discriminator is the seam where a future generic loader
/// dispatches to other handlers. First-party providers load through this exact path too, with no
/// privileged access over third-party ones.
/// </summary>
public sealed class ProviderPluginLoader
{
    /// <summary>Single-root scan (bundled-only). Kept for callers that don't dedup across roots.</summary>
    public IReadOnlyList<ProviderLoadResult> Load(string pluginsRoot) =>
        Load(PluginDiscovery.Discover(pluginsRoot, string.Empty));

    /// <summary>
    /// Loads already-discovered plugins (deduped across the bundled + user roots by
    /// <see cref="PluginDiscovery"/>). A folder whose manifest failed to parse becomes a failed result
    /// rather than being silently dropped, so the catalog can show it.
    /// </summary>
    public IReadOnlyList<ProviderLoadResult> Load(IEnumerable<DiscoveredPlugin> plugins)
    {
        var results = new List<ProviderLoadResult>();
        foreach (var plugin in plugins)
        {
            if (plugin.Manifest is not { } manifest)
            {
                results.Add(new ProviderLoadResult(plugin.Directory, null, null,
                    plugin.ManifestError ?? "Manifest could not be read."));
                continue;
            }

            // Only provider manifests are ours to load; tool/other types are skipped quietly here
            // (the tool loader picks those up) so the console isn't spammed for every tool plugin.
            if (manifest.Type != PluginManifest.Types.Provider)
            {
                continue;
            }

            results.Add(LoadOne(plugin.Directory, manifest));
        }

        return results;
    }

    private static ProviderLoadResult LoadOne(string dir, PluginManifest manifest)
    {
        try
        {
            if (!ProviderHostApi.IsCompatible(manifest.HostApiVersion))
            {
                return new ProviderLoadResult(dir, manifest.Id, null,
                    $"Plugin '{manifest.Id}' targets host API v{manifest.HostApiVersion}, " +
                    $"this host is v{ProviderHostApi.Version}.");
            }

            var assemblyPath = Path.Combine(dir, manifest.EntryAssembly);
            if (!File.Exists(assemblyPath))
            {
                return new ProviderLoadResult(dir, manifest.Id, null,
                    $"Entry assembly '{manifest.EntryAssembly}' not found in '{dir}'.");
            }

            var context = new ProviderLoadContext(assemblyPath);
            var assembly = context.LoadFromAssemblyPath(assemblyPath);

            var providerType = assembly.GetTypes()
                .FirstOrDefault(t => typeof(IDbProvider).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false });

            if (providerType is null)
            {
                return new ProviderLoadResult(dir, manifest.Id, null,
                    $"Assembly '{manifest.EntryAssembly}' has no public IDbProvider implementation.");
            }

            if (Activator.CreateInstance(providerType) is not IDbProvider provider)
            {
                return new ProviderLoadResult(dir, manifest.Id, null,
                    $"Could not instantiate '{providerType.FullName}' as IDbProvider.");
            }

            return new ProviderLoadResult(dir, manifest.Id, provider, null);
        }
        catch (Exception ex)
        {
            return new ProviderLoadResult(dir, null, null, ex.Message);
        }
    }
}
