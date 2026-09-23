using System.Text.Json;
using DataTray.Core;
using DataTray.Core.Connections;

namespace DataTray.Infrastructure.Secrets;

/// <summary>
/// Secrets in one JSON file under <see cref="AppPaths.Root"/> — the alternative to the OS vault a user picks
/// in onboarding (SE-292), and the only option at all on a machine with no keychain (headless or locked-down
/// Linux).
/// </summary>
/// <remarks>
/// This type does no crypto of its own: it is only ever the inner store of an
/// <see cref="EncryptingSecretStore"/> built with <c>requireKey</c>, so every value it is handed is already
/// AES-GCM encrypted under the master password and it can never be talked into writing a plaintext one.
/// Composing it any other way would put passwords on disk in the clear — see
/// <see cref="SecretStores.CreateFile"/>, which is the only place that does the composing.
///
/// Read-through on every call rather than a cached dictionary: the file is a handful of entries, and a second
/// instance (AllowMultipleInstances) writing to it must not be shadowed by a copy this one loaded at startup.
/// </remarks>
public sealed class FileSecretStore : ISecretStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();

    public FileSecretStore(string? path = null) => _path = path ?? AppPaths.File("secrets.json");

    public void Set(string key, string secret)
    {
        lock (_gate)
        {
            var all = Read();
            all[key] = secret;
            Write(all);
        }
    }

    public string? Get(string key)
    {
        lock (_gate)
        {
            return Read().GetValueOrDefault(key);
        }
    }

    public void Delete(string key)
    {
        lock (_gate)
        {
            var all = Read();
            if (all.Remove(key))
            {
                Write(all);
            }
        }
    }

    // A corrupt file is deliberately not swallowed: returning an empty dictionary would make the next Set
    // rewrite the file and take every other secret with it.
    private Dictionary<string, string> Read() =>
        File.Exists(_path)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path), Options) ?? []
            : [];

    private void Write(Dictionary<string, string> all)
    {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);

        var temp = Path.Combine(dir, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(all, Options));
        // Owner-only before it is moved into place, so the file is never briefly world-readable. Windows
        // inherits the profile folder's ACL and has no equivalent knob here.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temp, _path, overwrite: true);
    }
}
