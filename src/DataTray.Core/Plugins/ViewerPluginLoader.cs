using DataTray.Core.Localization;
using DataTray.Sdk.Localization;
using DataTray.Sdk.Viewers;

namespace DataTray.Core.Plugins;

/// <summary>Outcome of loading one viewer plugin folder: the viewers it contributed (an assembly may ship
/// several), its localizer (null when the plugin ships no translations), or an error explaining why it was
/// skipped.</summary>
public sealed record ViewerLoadResult(
    string PluginDirectory, string? Id, IReadOnlyList<IViewerPlugin> Viewers, IPluginLocalizer? Localizer, string? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// Loads <c>type: "viewer"</c> plugins. Mirrors <see cref="ToolPluginLoader"/> — same ALC
/// (<see cref="ProviderLoadContext"/>, which already shares the SDK + Avalonia with the host, so a viewer's
/// returned <c>Control</c> keeps one type identity across the boundary), same several-impls-per-assembly
/// rule, same localizer construction.
/// </summary>
public sealed class ViewerPluginLoader
{
    private readonly ILocalizer? _localizer;

    /// <summary>The <paramref name="localizer"/> is the live host localizer handed to each plugin's
    /// <see cref="PluginLocalizer"/>; pass null to load without plugin localization (tests/tooling).</summary>
    public ViewerPluginLoader(ILocalizer? localizer = null) => _localizer = localizer;

    /// <summary>Single-root scan (bundled-only). Kept for callers that don't dedup across roots.</summary>
    public IReadOnlyList<ViewerLoadResult> Load(string pluginsRoot) =>
        Load(PluginDiscovery.Discover(pluginsRoot, string.Empty));

    /// <summary>Loads the <c>type: "viewer"</c> plugins out of an already-discovered, deduped set.</summary>
    public IReadOnlyList<ViewerLoadResult> Load(IEnumerable<DiscoveredPlugin> plugins)
    {
        var results = new List<ViewerLoadResult>();
        foreach (var plugin in plugins)
        {
            // Skip non-viewer and unreadable folders quietly — the other loaders / catalog own those.
            if (plugin.Manifest is not { Type: PluginManifest.Types.Viewer } manifest)
            {
                continue;
            }

            results.Add(LoadOne(plugin.Directory, manifest));
        }

        return results;
    }

    private ViewerLoadResult LoadOne(string dir, PluginManifest manifest)
    {
        try
        {
            if (!ViewerHostApi.IsCompatible(manifest.HostApiVersion))
            {
                return new ViewerLoadResult(dir, manifest.Id, [], null,
                    $"Viewer '{manifest.Id}' targets viewer API v{manifest.HostApiVersion}, this host is v{ViewerHostApi.Version}.");
            }

            var assemblyPath = Path.Combine(dir, manifest.EntryAssembly);
            if (!File.Exists(assemblyPath))
            {
                return new ViewerLoadResult(dir, manifest.Id, [], null,
                    $"Entry assembly '{manifest.EntryAssembly}' not found in '{dir}'.");
            }

            var context = new ProviderLoadContext(assemblyPath);
            var assembly = context.LoadFromAssemblyPath(assemblyPath);

            var viewers = assembly.GetTypes()
                .Where(t => typeof(IViewerPlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
                .Select(Activator.CreateInstance)
                .OfType<IViewerPlugin>()
                .ToList();

            if (viewers.Count == 0)
            {
                return new ViewerLoadResult(dir, manifest.Id, [], null,
                    $"Assembly '{manifest.EntryAssembly}' has no public IViewerPlugin implementation.");
            }

            // Build the plugin's localizer from its embedded Lang/*.json (opt-in via manifest.localization).
            var localizer = _localizer is not null && !string.IsNullOrWhiteSpace(manifest.Localization)
                ? PluginLocalizer.TryLoad(assembly, manifest.Localization, _localizer,
                    warn => Console.Error.WriteLine($"[plugin] {manifest.Id}: {warn}"))
                : null;

            return new ViewerLoadResult(dir, manifest.Id, viewers, localizer, null);
        }
        catch (Exception ex)
        {
            return new ViewerLoadResult(dir, null, [], null, ex.Message);
        }
    }
}
