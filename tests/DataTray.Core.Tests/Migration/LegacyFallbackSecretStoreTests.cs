using DataTray.Core.Connections;
using DataTray.Infrastructure.Secrets;

namespace DataTray.Core.Tests.Migration;

/// <summary>
/// Covers the read-through migration of credentials written before the DataTray rename (SE-206). The
/// values at stake are database passwords, so the cases worth pinning down are the ones where a wrong
/// answer either loses a secret or resurrects a deleted one.
/// </summary>
public sealed class LegacyFallbackSecretStoreTests
{
    private sealed class FakeStore : ISecretStore
    {
        private readonly Dictionary<string, string> _values = [];

        public int Writes { get; private set; }

        /// <summary>Fails every operation — an entire vault that is unavailable.</summary>
        public bool Throws { get; set; }

        /// <summary>Fails writes only, so a read can succeed while the copy-forward does not.</summary>
        public bool ThrowsOnWrite { get; set; }

        public void Set(string key, string secret)
        {
            if (Throws || ThrowsOnWrite)
            {
                throw new InvalidOperationException("vault unavailable");
            }

            _values[key] = secret;
            Writes++;
        }

        public string? Get(string key) => Throws
            ? throw new InvalidOperationException("vault unavailable")
            : _values.GetValueOrDefault(key);

        public void Delete(string key)
        {
            if (Throws)
            {
                throw new InvalidOperationException("vault unavailable");
            }

            _values.Remove(key);
        }

        public bool Has(string key) => _values.ContainsKey(key);
    }

    [Fact]
    public void Returns_the_current_value_without_consulting_the_legacy_vault()
    {
        var current = new FakeStore();
        var legacy = new FakeStore { Throws = true };
        current.Set("conn:1:password", "new");

        var store = new LegacyFallbackSecretStore(current, legacy);

        Assert.Equal("new", store.Get("conn:1:password"));
    }

    [Fact]
    public void Finds_a_pre_rename_secret_and_copies_it_forward()
    {
        var current = new FakeStore();
        var legacy = new FakeStore();
        legacy.Set("conn:1:password", "hunter2");

        var store = new LegacyFallbackSecretStore(current, legacy);

        Assert.Equal("hunter2", store.Get("conn:1:password"));
        Assert.True(current.Has("conn:1:password"));
    }

    [Fact]
    public void Leaves_the_legacy_copy_in_place_so_an_older_build_still_connects()
    {
        var current = new FakeStore();
        var legacy = new FakeStore();
        legacy.Set("conn:1:password", "hunter2");

        new LegacyFallbackSecretStore(current, legacy).Get("conn:1:password");

        Assert.True(legacy.Has("conn:1:password"));
    }

    [Fact]
    public void Copies_forward_only_once()
    {
        var current = new FakeStore();
        var legacy = new FakeStore();
        legacy.Set("conn:1:password", "hunter2");
        var store = new LegacyFallbackSecretStore(current, legacy);

        store.Get("conn:1:password");
        store.Get("conn:1:password");

        Assert.Equal(1, current.Writes);
    }

    /// <summary>
    /// Without deleting both sides, removing a connection's password would hand it straight back on the
    /// next read — the legacy copy would be found and re-migrated.
    /// </summary>
    [Fact]
    public void A_deleted_secret_does_not_come_back_from_the_legacy_vault()
    {
        var current = new FakeStore();
        var legacy = new FakeStore();
        legacy.Set("conn:1:password", "hunter2");
        var store = new LegacyFallbackSecretStore(current, legacy);
        store.Get("conn:1:password");

        store.Delete("conn:1:password");

        Assert.Null(store.Get("conn:1:password"));
    }

    [Fact]
    public void Values_move_verbatim_so_ciphertext_survives_without_the_master_key()
    {
        var current = new FakeStore();
        var legacy = new FakeStore();
        legacy.Set("conn:1:password", "menc1:AAAA.BBBB.CCCC");

        var store = new LegacyFallbackSecretStore(current, legacy);

        Assert.Equal("menc1:AAAA.BBBB.CCCC", store.Get("conn:1:password"));
    }

    [Fact]
    public void An_unreadable_legacy_vault_reads_as_a_missing_secret_rather_than_throwing()
    {
        var store = new LegacyFallbackSecretStore(new FakeStore(), new FakeStore { Throws = true });

        Assert.Null(store.Get("conn:1:password"));
    }

    /// <summary>A failed copy-forward must still return the secret — the read is what the caller needs.</summary>
    [Fact]
    public void Returns_the_legacy_value_even_when_copying_it_forward_fails()
    {
        var legacy = new FakeStore();
        legacy.Set("conn:1:password", "hunter2");

        var store = new LegacyFallbackSecretStore(new FakeStore { ThrowsOnWrite = true }, legacy);

        Assert.Equal("hunter2", store.Get("conn:1:password"));
    }

    /// <summary>
    /// The decorator swallows failures of the *legacy* vault only. A broken current vault is a real
    /// fault and must surface, exactly as it did before this decorator existed — silently reporting
    /// "no secret" would look to the user like their saved password had vanished.
    /// </summary>
    [Fact]
    public void A_broken_current_vault_still_throws()
    {
        var store = new LegacyFallbackSecretStore(new FakeStore { Throws = true }, new FakeStore());

        Assert.Throws<InvalidOperationException>(() => store.Get("conn:1:password"));
    }

    [Fact]
    public void Writes_only_ever_reach_the_current_vault()
    {
        var current = new FakeStore();
        var legacy = new FakeStore();

        new LegacyFallbackSecretStore(current, legacy).Set("conn:1:password", "new");

        Assert.True(current.Has("conn:1:password"));
        Assert.False(legacy.Has("conn:1:password"));
    }
}
