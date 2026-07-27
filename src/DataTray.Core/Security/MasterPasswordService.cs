using DataTray.Core.Connections;
using DataTray.Core.Settings;

namespace DataTray.Core.Security;

/// <summary>
/// Coordinates the master-password lifecycle over the key provider, the settings store and the connection
/// secrets. Enable/change/disable each re-write every secret in one pass by reading it through the secret
/// store (decrypted with the current key), swapping the key, then writing it back (re-encrypted, or as
/// plaintext when disabling). The salt/verifier are only persisted after a successful pass, so an
/// interrupted run leaves the previous password valid rather than a half-migrated store.
/// </summary>
public sealed class MasterPasswordService(
    IAppSettingsStore settingsStore,
    IMasterKeyProvider keys,
    ConnectionService connections)
{
    /// <summary>True only when the feature is on AND its salt/verifier are present. A flag set without them
    /// (a corrupt/hand-edited settings.json) is treated as not-enabled, so the unlock gate never loops on a
    /// password it can never validate.</summary>
    public bool IsEnabled
    {
        get
        {
            var settings = settingsStore.Load();
            return settings is { MasterPasswordEnabled: true, MasterPasswordSalt: not null, MasterPasswordVerifier: not null };
        }
    }

    public bool IsUnlocked => keys.IsUnlocked;

    /// <summary>Apply the saved idle-lock interval to the key provider (call after a successful unlock).</summary>
    public void ApplyIdleTimeout()
    {
        var minutes = settingsStore.Load().MasterPasswordLockMinutes;
        keys.SetIdleTimeout(minutes > 0 ? TimeSpan.FromMinutes(minutes) : null);
    }

    /// <summary>Verify a typed password against the stored verifier and, on success, hold its key for the
    /// session. Returns true when there is no master password to unlock.</summary>
    public bool TryUnlock(string password)
    {
        var settings = settingsStore.Load();
        if (!settings.MasterPasswordEnabled || settings.MasterPasswordSalt is null || settings.MasterPasswordVerifier is null)
        {
            return true;
        }

        var key = MasterPasswordCrypto.DeriveKey(password, settings.MasterPasswordSalt);
        if (!MasterPasswordCrypto.CheckVerifier(key, settings.MasterPasswordVerifier))
        {
            return false;
        }

        keys.Unlock(key);
        ApplyIdleTimeout();
        return true;
    }

    /// <summary>Turn the feature on: derive a key from the new password and re-encrypt every existing
    /// secret with it.</summary>
    public void Enable(string newPassword)
    {
        var salt = MasterPasswordCrypto.NewSalt();
        var key = MasterPasswordCrypto.DeriveKey(newPassword, salt);

        // Read the current (plaintext) secrets before encryption is switched on.
        var plaintext = connections.ExportSecrets(); // no key yet → returns plaintext

        // Persist salt/verifier/flag FIRST, so a crash during the re-encryption below is recoverable: the
        // saved salt lets the key be re-derived at the next unlock, and the marker-per-value scheme keeps a
        // half-encrypted store readable (encrypted values decrypt, still-plaintext ones pass through). Doing
        // the writes first would risk orphaning encrypted secrets with no saved salt to ever read them.
        var settings = settingsStore.Load();
        settings.MasterPasswordEnabled = true;
        settings.MasterPasswordSalt = salt;
        settings.MasterPasswordVerifier = MasterPasswordCrypto.CreateVerifier(key);
        settingsStore.Save(settings);

        keys.Unlock(key);                             // store now encrypts on write
        foreach (var secret in plaintext)
        {
            connections.ImportSecret(secret);
        }

        ApplyIdleTimeout();
    }

    /// <summary>Change the password: verify the old one, then re-encrypt every secret with a fresh key.
    /// Returns false (and changes nothing) when the old password is wrong.</summary>
    public bool Change(string oldPassword, string newPassword)
    {
        var settings = settingsStore.Load();
        if (settings.MasterPasswordSalt is null || settings.MasterPasswordVerifier is null)
        {
            return false;
        }

        var oldKey = MasterPasswordCrypto.DeriveKey(oldPassword, settings.MasterPasswordSalt);
        if (!MasterPasswordCrypto.CheckVerifier(oldKey, settings.MasterPasswordVerifier))
        {
            return false;
        }

        keys.Unlock(oldKey);                          // reads decrypt with the old key
        var plaintext = connections.ExportSecrets();

        var newSalt = MasterPasswordCrypto.NewSalt();
        var newKey = MasterPasswordCrypto.DeriveKey(newPassword, newSalt);
        keys.Unlock(newKey);                          // writes encrypt with the new key
        foreach (var secret in plaintext)
        {
            connections.ImportSecret(secret);
        }

        settings.MasterPasswordSalt = newSalt;
        settings.MasterPasswordVerifier = MasterPasswordCrypto.CreateVerifier(newKey);
        settingsStore.Save(settings);
        ApplyIdleTimeout();
        return true;
    }

    /// <summary>Turn the feature off: verify the current password, then rewrite every secret as plaintext
    /// in the OS vault and clear the flag/salt/verifier. Returns false when the password is wrong.</summary>
    public bool Disable(string currentPassword)
    {
        var settings = settingsStore.Load();
        if (settings.MasterPasswordSalt is null || settings.MasterPasswordVerifier is null)
        {
            return false;
        }

        var key = MasterPasswordCrypto.DeriveKey(currentPassword, settings.MasterPasswordSalt);
        if (!MasterPasswordCrypto.CheckVerifier(key, settings.MasterPasswordVerifier))
        {
            return false;
        }

        keys.Unlock(key);                             // reads decrypt
        var plaintext = connections.ExportSecrets();
        keys.Lock();                                  // no key → writes are plaintext
        foreach (var secret in plaintext)
        {
            connections.ImportSecret(secret);
        }

        settings.MasterPasswordEnabled = false;
        settings.MasterPasswordSalt = null;
        settings.MasterPasswordVerifier = null;
        settingsStore.Save(settings);
        keys.SetIdleTimeout(null);
        return true;
    }
}
