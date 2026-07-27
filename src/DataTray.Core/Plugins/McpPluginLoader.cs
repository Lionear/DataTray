using DataTray.Sdk.Mcp;

namespace DataTray.Core.Plugins;

/// <summary>Outcome of loading one <c>type: "mcp"</c> plugin folder: the tool providers it contributed
/// (an assembly may ship several), or an error explaining why it was skipped.</summary>
public sealed record McpLoadResult(string PluginDirectory, string? Id, IReadOnlyList<IMcpToolProvider> Providers, string? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// Loads <c>type: "mcp"</c> plugins. Mirrors <see cref="ToolPluginLoader"/> and reuses
/// <see cref="ProviderLoadContext"/> (which shares the SDK with the host, so the plugin's
/// <see cref="IMcpToolProvider"/> and the <c>McpToolDefinition</c> delegates keep one type identity across
/// the boundary). Unlike a provider, an MCP assembly may contain several tool providers — all are loaded.
/// The host, not the plugin, owns the MCP server; a provider only contributes tools.
/// </summary>
public sealed class McpPluginLoader
{
    public IReadOnlyList<McpLoadResult> Load(IEnumerable<DiscoveredPlugin> plugins)
    {
        var results = new List<McpLoadResult>();
        foreach (var plugin in plugins)
        {
            if (plugin.Manifest is not { Type: PluginManifest.Types.Mcp } manifest)
            {
                continue;
            }

            results.Add(LoadOne(plugin.Directory, manifest));
        }

        return results;
    }

    private static McpLoadResult LoadOne(string dir, PluginManifest manifest)
    {
        try
        {
            if (!McpHostApi.IsCompatible(manifest.HostApiVersion))
            {
                return new McpLoadResult(dir, manifest.Id, [],
                    $"MCP plugin '{manifest.Id}' targets MCP API v{manifest.HostApiVersion}, this host is v{McpHostApi.Version}.");
            }

            var assemblyPath = Path.Combine(dir, manifest.EntryAssembly);
            if (!File.Exists(assemblyPath))
            {
                return new McpLoadResult(dir, manifest.Id, [],
                    $"Entry assembly '{manifest.EntryAssembly}' not found in '{dir}'.");
            }

            var context = new ProviderLoadContext(assemblyPath);
            var assembly = context.LoadFromAssemblyPath(assemblyPath);

            var providers = assembly.GetTypes()
                .Where(t => typeof(IMcpToolProvider).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
                .Select(Activator.CreateInstance)
                .OfType<IMcpToolProvider>()
                .ToList();

            return providers.Count == 0
                ? new McpLoadResult(dir, manifest.Id, [], $"Assembly '{manifest.EntryAssembly}' has no public IMcpToolProvider implementation.")
                : new McpLoadResult(dir, manifest.Id, providers, null);
        }
        catch (Exception ex)
        {
            return new McpLoadResult(dir, null, [], ex.Message);
        }
    }
}
