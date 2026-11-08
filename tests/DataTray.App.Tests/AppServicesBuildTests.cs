using System.Globalization;
using DataTray.App.DependencyInjection;
using DataTray.App.Localization;
using DataTray.Core.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace DataTray.App.Tests;

/// <summary>Build reads DATATRAY_APPDATA, which is process-wide: these tests run alone.</summary>
[CollectionDefinition(nameof(AppDataEnvironment), DisableParallelization = true)]
public sealed class AppDataEnvironment;

[Collection(nameof(AppDataEnvironment))]
public sealed class AppServicesBuildTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "se297-appdata-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previous = Environment.GetEnvironmentVariable("DATATRAY_APPDATA");
    private readonly ResxLocalizer _loc = new();

    public AppServicesBuildTests()
    {
        Environment.SetEnvironmentVariable("DATATRAY_APPDATA", _root);
        _loc.SetCulture(CultureInfo.GetCultureInfo("en"));

        // A plugin installed from the store: it never loads (no assembly), but discovery lists it.
        var plugin = Path.Combine(_root, "Lionear", "DataTray", "plugins", "acme-store");
        Directory.CreateDirectory(plugin);
        File.WriteAllText(Path.Combine(plugin, "plugin.json"),
            """{ "schemaVersion": 1, "id": "acme-store", "type": "tool", "name": "Acme", "version": "1.0.0", "hostApiVersion": 5, "entryAssembly": "Acme.dll" }""");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DATATRAY_APPDATA", _previous);
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    [Fact] // What the splash shows under "Loading your plugins" while the loaders run.
    public void Build_reports_each_plugin_kind_as_it_loads()
    {
        var reported = new List<string>();

        AppServices.Build(_loc, new InlineProgress(reported.Add));

        Assert.Equal(
            ["Engines · 1 of 5", "Tools · 2 of 5", "Viewers · 3 of 5", "MCP tools · 4 of 5", "Extensions · 5 of 5"],
            reported);
    }

    [Theory] // "Continue without store plugins" must not even look in the per-user folder.
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Bundled_only_leaves_store_plugins_out(bool bundledOnly, bool storePluginListed)
    {
        var services = AppServices.Build(_loc, bundledPluginsOnly: bundledOnly);

        var installed = services.GetRequiredService<PluginCatalogService>().Installed;
        Assert.Equal(storePluginListed, installed.Any(p => p.Id == "acme-store"));
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
