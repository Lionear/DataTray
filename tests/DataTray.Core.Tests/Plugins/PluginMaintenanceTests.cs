using DataTray.Core.Plugins;

namespace DataTray.Core.Tests.Plugins;

public class PluginMaintenanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "se-maint-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    // Writes a plugin folder with a single marker file whose content is the "version", so a test can
    // assert which copy ended up in the target slot after the swap.
    private string Plugin(string name, string version)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "marker.txt"), version);
        return dir;
    }

    private string Version(string name) => File.ReadAllText(Path.Combine(_root, name, "marker.txt"));

    private sealed class FakeStateStore : IPluginStateStore
    {
        private readonly Dictionary<string, PluginStateEntry> _entries = new(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, PluginStateEntry> GetAll() => _entries;
        public PluginStateEntry Get(string id) => _entries.TryGetValue(id, out var e) ? e : new PluginStateEntry();
        public void Save(string id, PluginStateEntry entry) => _entries[id] = entry;
        public void Remove(string id) => _entries.Remove(id);
    }

    [Fact]
    public void Promotes_staged_next_and_keeps_old_as_prev()
    {
        Plugin("acme", "1.0.0");
        Plugin("acme.next", "2.0.0");

        var failed = PluginMaintenance.ApplyPending(new FakeStateStore(), _root);

        Assert.Empty(failed);
        Assert.Equal("2.0.0", Version("acme"));          // new version is live
        Assert.Equal("1.0.0", Version("acme.prev"));     // old kept as rollback backup
        Assert.False(Directory.Exists(Path.Combine(_root, "acme.next")));
    }

    [Fact]
    public void Fresh_install_moves_next_into_place()
    {
        Plugin("acme.next", "1.0.0");   // no existing <id>

        var failed = PluginMaintenance.ApplyPending(new FakeStateStore(), _root);

        Assert.Empty(failed);
        Assert.Equal("1.0.0", Version("acme"));
        Assert.False(Directory.Exists(Path.Combine(_root, "acme.next")));
    }

    [Fact]
    public void A_blocked_prev_backup_never_stalls_the_update()
    {
        // Regression (SE plugin-store bugs): a stuck <id>.prev used to leave the OLD <id> in place, so
        // "I updated but the old plugin is still here after restart" persisted across every restart. The
        // demote-to-.prev must fall back to dropping the current copy so the staged version still applies.
        Plugin("acme", "1.0.0");
        Plugin("acme.next", "2.0.0");
        // A *file* at <id>.prev: Directory.Delete/Move both refuse it, so the demote step fails — the
        // exact condition that used to abort the swap. The fallback should delete the old copy instead.
        File.WriteAllText(Path.Combine(_root, "acme.prev"), "blocker");

        var failed = PluginMaintenance.ApplyPending(new FakeStateStore(), _root);

        Assert.Empty(failed);
        Assert.Equal("2.0.0", Version("acme"));          // update applied despite the blocked backup
        Assert.False(Directory.Exists(Path.Combine(_root, "acme.next")));
    }

    [Fact]
    public void Pending_remove_deletes_the_folder_and_its_staging()
    {
        Plugin("acme", "1.0.0");
        Plugin("acme.next", "2.0.0");
        Plugin("acme.prev", "0.9.0");
        var store = new FakeStateStore();
        store.Save("acme", new PluginStateEntry { Pending = PluginPendingAction.Remove });

        var failed = PluginMaintenance.ApplyPending(store, _root);

        Assert.Empty(failed);
        Assert.False(Directory.Exists(Path.Combine(_root, "acme")));
        Assert.False(Directory.Exists(Path.Combine(_root, "acme.next")));
        Assert.False(Directory.Exists(Path.Combine(_root, "acme.prev")));
        Assert.Empty(store.GetAll());
    }
}
