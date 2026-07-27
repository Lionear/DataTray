using DataTray.Core.Localization;
using DataTray.Sdk.Localization;
using DataTray.Sdk.Tools;

namespace DataTray.Core.Plugins;

/// <summary>Outcome of loading one tool plugin folder: the tools it contributed (an assembly may ship
/// several), its localizer (null when the plugin ships no translations), or an error explaining why it was
/// skipped.</summary>
public sealed record ToolLoadResult(
    string PluginDirectory, string? Id, IReadOnlyList<IToolPlugin> Tools, IPluginLocalizer? Localizer, string? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// Loads <c>type: "tool"</c> plugins. Mirrors <see cref="ProviderPluginLoader"/> and reuses
/// <see cref="ProviderLoadContext"/> (which already shares the SDK + Avalonia with the host, so a tool's
/// declared types and any Route-B view keep one type identity across the boundary). Unlike a provider,
/// one tool assembly may contain several <see cref="IToolPlugin"/> implementations — all are instantiated.
/// </summary>
public sealed class ToolPluginLoader
{
    private readonly ILocalizer? _localizer;

    /// <summary>The <paramref name="localizer"/> is the live host localizer handed to each plugin's
    /// <see cref="PluginLocalizer"/>; pass null to load without plugin localization (tests/tooling).</summary>
    public ToolPluginLoader(ILocalizer? localizer = null) => _localizer = localizer;

    /// <summary>Single-root scan (bundled-only). Kept for callers that don't dedup across roots.</summary>
    public IReadOnlyList<ToolLoadResult> Load(string pluginsRoot) =>
        Load(PluginDiscovery.Discover(pluginsRoot, string.Empty));

    /// <summary>Loads the <c>type: "tool"</c> plugins out of an already-discovered, deduped set.</summary>
    public IReadOnlyList<ToolLoadResult> Load(IEnumerable<DiscoveredPlugin> plugins)
    {
        var results = new List<ToolLoadResult>();
        foreach (var plugin in plugins)
        {
            // Skip non-tool and unreadable folders quietly — the provider loader / catalog own those.
            if (plugin.Manifest is not { Type: PluginManifest.Types.Tool } manifest)
            {
                continue;
            }

            results.Add(LoadOne(plugin.Directory, manifest));
        }

        return results;
    }

    private ToolLoadResult LoadOne(string dir, PluginManifest manifest)
    {
        try
        {
            if (!ToolHostApi.IsCompatible(manifest.HostApiVersion))
            {
                return new ToolLoadResult(dir, manifest.Id, [], null,
                    $"Tool '{manifest.Id}' targets tool API v{manifest.HostApiVersion}, this host is v{ToolHostApi.Version}.");
            }

            var assemblyPath = Path.Combine(dir, manifest.EntryAssembly);
            if (!File.Exists(assemblyPath))
            {
                return new ToolLoadResult(dir, manifest.Id, [], null,
                    $"Entry assembly '{manifest.EntryAssembly}' not found in '{dir}'.");
            }

            var context = new ProviderLoadContext(assemblyPath);
            var assembly = context.LoadFromAssemblyPath(assemblyPath);

            var tools = assembly.GetTypes()
                .Where(t => typeof(IToolPlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
                .Select(Activator.CreateInstance)
                .OfType<IToolPlugin>()
                .ToList();

            if (tools.Count == 0)
            {
                return new ToolLoadResult(dir, manifest.Id, [], null,
                    $"Assembly '{manifest.EntryAssembly}' has no public IToolPlugin implementation.");
            }

            // Build the plugin's localizer from its embedded Lang/*.json (opt-in via manifest.localization).
            var localizer = _localizer is not null && !string.IsNullOrWhiteSpace(manifest.Localization)
                ? PluginLocalizer.TryLoad(assembly, manifest.Localization, _localizer,
                    warn => Console.Error.WriteLine($"[plugin] {manifest.Id}: {warn}"))
                : null;

            return new ToolLoadResult(dir, manifest.Id, tools, localizer, null);
        }
        catch (Exception ex)
        {
            return new ToolLoadResult(dir, null, [], null, ex.Message);
        }
    }
}
