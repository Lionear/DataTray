using System.Security.Cryptography;
using DataTray.Core.Connections;
using DataTray.Core.Security;

namespace DataTray.Infrastructure.Secrets;

/// <summary>
/// Wraps the OS credential store with an optional app-level encryption layer keyed by the master password.
/// When the session holds a key, secrets are AES-GCM encrypted before they reach the OS vault and decrypted
/// on the way out; without a key they pass through as plaintext. Encrypted values carry a marker, so a
/// mixed store (mid enable/disable migration, or a locked session) stays correct per value: plaintext is
/// returned as-is, and an encrypted value can only be read while unlocked.
/// </summary>
/// <param name="requireKey">
/// Over the file store (SE-292) there is no OS vault underneath to make plaintext merely weaker rather than
/// wrong, so writing without a key is refused instead of passing the secret through. The startup gate makes
/// this unreachable in practice — a file store implies a master password, which implies an unlock before the
/// main window is usable — which is why it throws rather than dropping the write on the floor.
/// </param>
public sealed class EncryptingSecretStore(ISecretStore inner, IMasterKeyProvider keys, bool requireKey = false)
    : ISecretStore
{
    public void Set(string key, string secret)
    {
        if (keys.Key is not { } k)
        {
            if (requireKey)
            {
                throw new InvalidOperationException(
                    "The encrypted file store needs an unlocked master password before a secret can be saved.");
            }

            inner.Set(key, secret);
            return;
        }

        inner.Set(key, MasterPasswordCrypto.EncryptSecret(k, secret));
    }

    public string? Get(string key)
    {
        var value = inner.Get(key);
        if (value is null || !MasterPasswordCrypto.IsEncrypted(value))
        {
            return value; // plaintext (feature off, or written before it was enabled)
        }

        // Encrypted: readable only while the session is unlocked and the key matches. A locked session or a
        // key that can't decrypt this value (e.g. a stale/interrupted migration) yields "unavailable" rather
        // than throwing — a failed decrypt must never crash a connection resolve.
        if (keys.Key is not { } k)
        {
            return null;
        }

        try
        {
            return MasterPasswordCrypto.DecryptSecret(k, value);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public void Delete(string key) => inner.Delete(key);
}
