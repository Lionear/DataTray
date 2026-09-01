using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataTray.Core.Plugins;
using DataTray.Core.Store;

namespace DataTray.Core.Tests.Store;

public class PluginUpdateServiceTests
{
    // Provider host API is Current = ProviderHostApi.Version (26 at the time of writing); MinHostApiVersion
    // 27 is beyond it (held back), 23 is within the accepted window (compatible).
    private const int BeyondHost = 999;
    private const int WithinHost = 23;

    private static PluginUpdateService Service(params string[] pinnedIds) =>
        new(new UnusedInstaller(), new FakePins(pinnedIds));

    private static InstalledPlugin Installed(string id, string version, PluginOrigin origin = PluginOrigin.UserInstalled) =>
        new(id, id, version, "provider", origin, Enabled: true, PluginPendingAction.None, Loaded: true, LoadError: null, Directory: "");

    private static StoreCatalog Catalog(params StoreEntry[] entries) =>
        new([.. entries.Select(e => new CatalogEntry(e, "url", "src"))], [], []);

    private static StoreEntry Entry(string id, params (string Version, int MinApi)[] versions) => new()
    {
        Id = id,
        Name = id,
        Type = "provider",
        Versions = [.. versions.Select(v => new StoreVersion
        {
            Version = v.Version,
            MinHostApiVersion = v.MinApi,
            DownloadUrl = "",
            Sha256 = "",
        })],
    };

    [Fact]
    public void Held_back_when_newest_version_needs_a_higher_host_api()
    {
        var catalog = Catalog(Entry("p", ("1.0.0", WithinHost), ("1.1.0", WithinHost), ("2.0.0", BeyondHost)));

        var held = Service().DetectHeldBack([Installed("p", "1.0.0")], catalog);

        var one = Assert.Single(held);
        Assert.Equal("2.0.0", one.TargetVersion);
        Assert.Equal(BeyondHost, one.RequiredHostApiVersion);
    }

    [Fact]
    public void Not_held_back_when_newest_is_compatible()
    {
        var catalog = Catalog(Entry("p", ("1.0.0", WithinHost), ("1.1.0", WithinHost)));

        Assert.Empty(Service().DetectHeldBack([Installed("p", "1.0.0")], catalog));
    }

    [Fact]
    public void Not_held_back_when_no_newer_version()
    {
        var catalog = Catalog(Entry("p", ("1.0.0", WithinHost)));

        Assert.Empty(Service().DetectHeldBack([Installed("p", "1.0.0")], catalog));
    }

    [Fact]
    public void Pinned_plugins_are_excluded()
    {
        var catalog = Catalog(Entry("p", ("1.0.0", WithinHost), ("2.0.0", BeyondHost)));

        Assert.Empty(Service(pinnedIds: "p").DetectHeldBack([Installed("p", "1.0.0")], catalog));
    }

    [Fact]
    public void Bundled_plugins_are_excluded()
    {
        var catalog = Catalog(Entry("p", ("1.0.0", WithinHost), ("2.0.0", BeyondHost)));

        Assert.Empty(Service().DetectHeldBack([Installed("p", "1.0.0", PluginOrigin.Bundled)], catalog));
    }

    // The publish workflow (SE-272) stamps a build number onto the manifest version, so everything it
    // publishes is four-segment: 1.2.0 in plugins/<name>/plugin.json becomes 1.2.0.<run number> in the
    // store and in the zip's manifest. Nothing about that is special-cased — it works because
    // SemVer.Compare pads the shorter core with zeros — which is exactly why it is pinned here: a
    // "tidy-up" of that padding would silently stop every published plugin from ever offering an update.
    [Fact]
    public void A_build_number_on_the_same_base_version_is_an_update()
    {
        var catalog = Catalog(Entry("p", ("1.2.0", WithinHost), ("1.2.0.41", WithinHost)));

        var update = Assert.Single(Service().DetectUpdates([Installed("p", "1.2.0")], catalog));

        Assert.Equal("1.2.0.41", update.Target.Version);
    }

    [Fact]
    public void The_build_number_that_is_installed_is_not_offered_again()
    {
        var catalog = Catalog(Entry("p", ("1.2.0", WithinHost), ("1.2.0.41", WithinHost)));

        Assert.Empty(Service().DetectUpdates([Installed("p", "1.2.0.41")], catalog));
    }

    [Fact]
    public void A_higher_base_version_beats_a_build_number_on_the_previous_one()
    {
        var catalog = Catalog(Entry("p", ("1.2.0.41", WithinHost), ("1.3.0.7", WithinHost)));

        var update = Assert.Single(Service().DetectUpdates([Installed("p", "1.2.0.41")], catalog));

        Assert.Equal("1.3.0.7", update.Target.Version);
    }

    private sealed class FakePins(params string[] pinnedIds) : IPluginPinStore
    {
        private readonly Dictionary<string, string> _pins = pinnedIds.ToDictionary(id => id, _ => "0.0.0");
        public string? GetPin(string pluginId) => _pins.GetValueOrDefault(pluginId);
        public IReadOnlyDictionary<string, string> GetAll() => _pins;
        public void Pin(string pluginId, string version) { }
        public void Unpin(string pluginId) { }
    }

    private sealed class UnusedInstaller : IPluginInstaller
    {
        public Task<InstallOutcome> InstallAsync(StoreEntry entry, StoreVersion version, IProgress<InstallProgress>? progress, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<InstallOutcome> InstallFromFileAsync(string zipPath, IProgress<InstallProgress>? progress, CancellationToken ct)
            => throw new NotSupportedException();
        public InstallOutcome RequestRollback(string pluginId) => throw new NotSupportedException();
    }
}
