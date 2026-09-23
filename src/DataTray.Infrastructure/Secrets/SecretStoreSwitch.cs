using DataTray.Core.Connections;

namespace DataTray.Infrastructure.Secrets;

/// <summary>
/// The <see cref="ISecretStore"/> everything else holds, with the store that actually does the work behind a
/// swappable reference. Onboarding's Security step (SE-292) picks between the OS vault and the encrypted file
/// store while the app is already running, and every consumer of the store is a singleton that resolved it at
/// startup — without this, the choice would only take effect after a restart, which is exactly when the next
/// wizard step is writing the first password.
/// </summary>
public sealed class SecretStoreSwitch(ISecretStore initial) : ISecretStore
{
    private volatile ISecretStore _current = initial;

    /// <summary>The store secrets go to from now on. Nothing is migrated — see SE-292 point 4.</summary>
    public void Use(ISecretStore store) => _current = store;

    public void Set(string key, string secret) => _current.Set(key, secret);

    public string? Get(string key) => _current.Get(key);

    public void Delete(string key) => _current.Delete(key);
}
