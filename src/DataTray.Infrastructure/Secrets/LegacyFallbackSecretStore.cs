using DataTray.Core.Connections;

namespace DataTray.Infrastructure.Secrets;

/// <summary>
/// Reads through to the pre-rename vault (SE-206). A key missing from the DataTray service is looked up
/// under the SQL Explorer service, and a hit is copied forward before being returned.
/// </summary>
/// <remarks>
/// <para>
/// Lazy rather than an up-front sweep, because there is no portable way to enumerate the three vaults:
/// Windows has CredEnumerate, macOS would need new Security.framework interop, and Linux would shell out
/// to <c>secret-tool search</c>. Reconstructing the keys instead — they are all
/// <c>conn:{id}:{field}</c> — needs both connections.json and each provider's field definitions, which
/// means a connection whose provider plugin is disabled or missing would be skipped and its secret
/// quietly left behind. Reading through has none of those problems: it is one code path on all three
/// platforms, it needs no interop, and a key migrates the moment anything asks for it, whatever it is.
/// </para>
/// <para>
/// Copy, not move — deliberately the same choice as the app data folder. Deleting the old entry would
/// leave a SQL Explorer build with a connection it can no longer open, which is exactly the fallback
/// that copying the data folder was meant to preserve. The cost is that a secret exists twice in the OS
/// vault until the user clears the old entries.
/// </para>
/// <para>
/// Values move verbatim. When a master password is set they are ciphertext (the <c>menc1:</c> envelope
/// added by <see cref="EncryptingSecretStore"/>, which sits *above* this decorator), so nothing here
/// needs the master key — and migration therefore works at startup, long before the user unlocks.
/// </para>
/// </remarks>
public sealed class LegacyFallbackSecretStore(ISecretStore current, ISecretStore legacy) : ISecretStore
{
    public void Set(string key, string secret) => current.Set(key, secret);

    public string? Get(string key)
    {
        var value = current.Get(key);
        if (value is not null)
        {
            return value;
        }

        string? legacyValue;
        try
        {
            legacyValue = legacy.Get(key);
        }
        catch
        {
            // The old vault being unreadable is not a reason to fail the read. Treat it as "no secret
            // there" — the caller then sees the same missing-credential path as any other absent key.
            return null;
        }

        if (legacyValue is null)
        {
            return null;
        }

        try
        {
            current.Set(key, legacyValue);
        }
        catch
        {
            // Copying forward is an optimisation, not the contract. If the write fails we still return
            // the value we found, and the next read tries again.
        }

        return legacyValue;
    }

    /// <summary>
    /// Deletes from both vaults. A secret the user removed must not come back on the next read, which is
    /// exactly what would happen if the legacy copy survived a delete.
    /// </summary>
    public void Delete(string key)
    {
        current.Delete(key);

        try
        {
            legacy.Delete(key);
        }
        catch
        {
            // Best-effort: the current vault is the one that matters for correctness here.
        }
    }
}
