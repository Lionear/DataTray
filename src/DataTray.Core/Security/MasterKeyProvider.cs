using System.Security.Cryptography;

namespace DataTray.Core.Security;

/// <summary>
/// Default <see cref="IMasterKeyProvider"/>: keeps the key in a private field and, when an idle timeout is
/// set, clears it after that long without a <see cref="Key"/> read. The timer is rearmed on every key read
/// (every secret access), so an active session never locks; an idle one does and fires <see cref="Locked"/>
/// so the app can put the unlock prompt back up.
/// </summary>
public sealed class MasterKeyProvider : IMasterKeyProvider, IDisposable
{
    private readonly object _gate = new();
    private byte[]? _key;
    private TimeSpan? _idle;
    private Timer? _timer;

    public event Action? Locked;

    public bool IsUnlocked
    {
        get { lock (_gate) { return _key is not null; } }
    }

    public byte[]? Key
    {
        get
        {
            lock (_gate)
            {
                if (_key is not null)
                {
                    Rearm();
                }
                return _key;
            }
        }
    }

    public void Unlock(byte[] key)
    {
        lock (_gate)
        {
            _key = key;
            Rearm();
        }
    }

    public void Lock()
    {
        lock (_gate)
        {
            if (_key is not null)
            {
                CryptographicOperations.ZeroMemory(_key);
                _key = null;
            }
            _timer?.Dispose();
            _timer = null;
        }
    }

    public void SetIdleTimeout(TimeSpan? timeout)
    {
        lock (_gate)
        {
            _idle = timeout is { TotalMilliseconds: > 0 } ? timeout : null;
            Rearm();
        }
    }

    // Restart the idle timer from now. Caller holds _gate.
    private void Rearm()
    {
        _timer?.Dispose();
        _timer = null;
        if (_key is null || _idle is not { } span)
        {
            return;
        }

        _timer = new Timer(_ => OnIdleElapsed(), null, span, Timeout.InfiniteTimeSpan);
    }

    private void OnIdleElapsed()
    {
        lock (_gate)
        {
            if (_key is null)
            {
                return;
            }
            CryptographicOperations.ZeroMemory(_key);
            _key = null;
            _timer?.Dispose();
            _timer = null;
        }

        Locked?.Invoke();
    }

    public void Dispose() => Lock();
}
