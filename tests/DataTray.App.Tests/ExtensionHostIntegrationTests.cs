using System.Globalization;
using DataTray.App.DependencyInjection;
using DataTray.App.Localization;
using DataTray.App.ViewModels;
using DataTray.Core.Plugins;
using DataTray.Core.Providers;
using DataTray.Core.Store;
using DataTray.Sdk.Extensibility;
using DataTray.Sdk.Settings;
using DataTray.Sdk.Shortcuts;
using Microsoft.Extensions.DependencyInjection;

namespace DataTray.App.Tests;

[Collection(nameof(AppDataEnvironment))]
public sealed class ExtensionHostIntegrationTests : IDisposable, IClassFixture<HeadlessDrawing>
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "se299-appdata-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previous = Environment.GetEnvironmentVariable("DATATRAY_APPDATA");
    private readonly ResxLocalizer _loc = new();

    public ExtensionHostIntegrationTests()
    {
        Environment.SetEnvironmentVariable("DATATRAY_APPDATA", _root);
        _loc.SetCulture(CultureInfo.GetCultureInfo("en"));

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

    [Fact]
    public void CollectPluginShortcuts_IncludesExtensions()
    {
        var shortcuts = AppServices.CollectPluginShortcuts([], [], [Activation(new FakeExtension())]);

        var shortcut = Assert.Single(shortcuts);
        Assert.Equal(("builder:run", "builder", "Builder", "Run"), (shortcut.Id, shortcut.PluginId, shortcut.PluginTitle, shortcut.Title));
    }

    [Fact]
    public void PluginCatalog_IncludesExtensionWithSettings()
    {
        var plugins = HeadlessDrawing.Run(() => ActivatorUtilities.CreateInstance<SettingsViewModel>(
            AppServices.Build(_loc), (IReadOnlyList<SubsystemActivation>)[Activation(new FakeExtension())])
            .Plugins.Select(p => p.PluginId).ToArray());

        Assert.Equal(["builder"], plugins);
    }

    [Fact]
    public async Task Uninstall_Cancelled_DoesNothing()
    {
        var services = AppServices.Build(_loc);
        var pins = services.GetRequiredService<IPluginPinStore>();
        pins.Pin("acme-store", "1.0.0");
        var catalog = services.GetRequiredService<PluginCatalogService>();
        var item = new InstalledListItem(catalog.Installed.Single(p => p.Id == "acme-store"), hasRollback: false);
        var vm = services.GetRequiredService<PluginStoreViewModel>();
        vm.ConfirmRequested = (_, _) => Task.FromResult(false);

        await vm.UninstallCommand.ExecuteAsync(item);

        Assert.Equal(
            (PluginPendingAction.None, "1.0.0", false),
            (item.Pending, pins.GetPin("acme-store"), vm.RestartRequired));
    }

    [Fact]
    public async Task Uninstall_Confirmed_StagesRemoval()
    {
        var services = AppServices.Build(_loc);
        var catalog = services.GetRequiredService<PluginCatalogService>();
        var item = new InstalledListItem(catalog.Installed.Single(p => p.Id == "acme-store"), hasRollback: false);
        var asked = new List<(string Title, string Message)>();
        var vm = services.GetRequiredService<PluginStoreViewModel>();
        vm.ConfirmRequested = (title, message) =>
        {
            asked.Add((title, message));
            return Task.FromResult(true);
        };

        await vm.UninstallCommand.ExecuteAsync(item);

        Assert.Equal(
            (PluginPendingAction.Remove, "Uninstall Acme?|It is removed the next time DataTray starts."),
            (item.Pending, string.Join(';', asked.Select(a => $"{a.Title}|{a.Message}"))));
    }

    [Theory]
    [InlineData("query-pane", "Adds a pane to query tabs")]
    [InlineData("schema", "Reads the table and column names of the connection a query tab uses")]
    public void CapabilityCatalog_DescribesQueryPaneCapabilities(string key, string description)
    {
        Assert.Equal(description, CapabilityCatalog.Describe(key, _loc).Description);
    }

    private static SubsystemActivation Activation(ISubsystemPlugin plugin) =>
        new("builder", [], plugin, null, "Builder");

    private sealed class FakeExtension : ISubsystemPlugin, IShortcutContributor, IPluginSettings
    {
        public IReadOnlyList<ShortcutContribution> Shortcuts { get; } =
            [new("run", "Run", "Ctrl+Alt+R", _ => Task.CompletedTask)];

        public IReadOnlyList<PluginSettingField> SettingsFields { get; } = [new("limit", "Limit")];

        public void Initialize(IPluginRuntimeContext context)
        {
        }
    }
}
