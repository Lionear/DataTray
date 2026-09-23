using DataTray.Core.Security;
using DataTray.Infrastructure.Secrets;

namespace DataTray.Core.Tests.Security;

/// <summary>
/// SE-292's file store. The store itself is a dictionary on disk; what matters is the composition around it,
/// because that is the only thing standing between a password and a plaintext file.
/// </summary>
public class FileSecretStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"datatray-secrets-{Guid.NewGuid():N}.json");

    [Fact]
    public void Secrets_survive_a_round_trip_and_a_new_instance()
    {
        var store = new FileSecretStore(_path);

        store.Set("conn/1/password", "hunter2");
        store.Set("conn/2/password", "correct horse");

        Assert.Equal("hunter2", new FileSecretStore(_path).Get("conn/1/password"));

        store.Delete("conn/1/password");

        Assert.Null(new FileSecretStore(_path).Get("conn/1/password"));
        Assert.Equal("correct horse", new FileSecretStore(_path).Get("conn/2/password"));
    }

    [Fact] // The whole point of the store: what lands on disk is ciphertext, and the plaintext is nowhere in
           // the file — not under another key, not as a leftover from an earlier write.
    public void What_reaches_the_file_is_encrypted_and_only_readable_while_unlocked()
    {
        var keys = new MasterKeyProvider();
        keys.Unlock(MasterPasswordCrypto.DeriveKey("master", MasterPasswordCrypto.NewSalt()));
        var store = SecretStores.CreateFile(keys, _path);

        store.Set("conn/1/password", "hunter2");

        Assert.DoesNotContain("hunter2", File.ReadAllText(_path));
        Assert.Equal("hunter2", store.Get("conn/1/password"));

        keys.Lock();
        Assert.Null(store.Get("conn/1/password"));           // locked: unavailable, not a crash
    }

    [Fact] // There is no OS vault underneath to make a plaintext write merely weaker — it would be a password
           // in a file, in the clear. Refuse it instead.
    public void Writing_without_a_master_key_is_refused_rather_than_written_in_plaintext()
    {
        var store = SecretStores.CreateFile(new MasterKeyProvider(), _path);

        Assert.Throws<InvalidOperationException>(() => store.Set("conn/1/password", "hunter2"));
        Assert.False(File.Exists(_path));
    }

    public void Dispose() => File.Delete(_path);
}
