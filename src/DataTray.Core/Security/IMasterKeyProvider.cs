namespace DataTray.Core.Security;

/// <summary>
/// Holds the derived master-password AES key in memory while the session is unlocked. The key is never
/// persisted; it is cleared on <see cref="Lock"/>, on app exit, and after the configured idle timeout.
/// Reading <see cref="Key"/> counts as activity and resets the idle timer.
/// </summary>
public interface IMasterKeyProvider
{
    bool IsUnlocked { get; }

    /// <summary>The current key, or null when locked. Getting it resets the idle-timeout timer.</summary>
    byte[]? Key { get; }

    void Unlock(byte[] key);

    void Lock();

    /// <summary>Set the idle auto-lock interval; null disables it (only restart re-locks).</summary>
    void SetIdleTimeout(TimeSpan? timeout);

    /// <summary>Raised when the idle timeout elapses and the key is cleared, so the UI can re-prompt.</summary>
    event Action? Locked;
}
