using System.Text.Json;
using DataTray.Core;
using DataTray.Core.Store;

namespace DataTray.Infrastructure.Store;

/// <summary>
/// Stores per-plugin version pins as a JSON <c>{id: version}</c> map (<c>plugin-pins.json</c>) next to
/// connections.json. Writes are atomic (temp file + replace) and a corrupt/unreadable file degrades to
/// empty rather than crashing — mirrors the other JSON stores. Pin ids are ordinal case-sensitive
/// (they match <c>plugin.json</c>'s <c>id</c> which is itself lower-case-with-hyphens by convention).
/// </summary>
public sealed class JsonPluginPinStore : IPluginPinStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true
    };

    private readonly string _path;

    public JsonPluginPinStore(string? path = null)
    {
        _path = path ?? DefaultPath();
    }

    private static string DefaultPath()
    {
        var dir = AppPaths.Root;
        return Path.Combine(dir, "plugin-pins.json");
    }

    public string? GetPin(string pluginId) => Read().TryGetValue(pluginId, out var v) ? v : null;

    public IReadOnlyDictionary<string, string> GetAll() => Read();

    public void Pin(string pluginId, string version)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || string.IsNullOrWhiteSpace(version))
        {
            return;
        }

        var all = new Dictionary<string, string>(Read(), StringComparer.Ordinal)
        {
            [pluginId] = version
        };
        Write(all);
    }

    public void Unpin(string pluginId)
    {
        var all = Read();
        if (!all.ContainsKey(pluginId))
        {
            return;
        }

        var updated = new Dictionary<string, string>(all, StringComparer.Ordinal);
        updated.Remove(pluginId);
        Write(updated);
    }

    private Dictionary<string, string> Read()
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            using var stream = File.OpenRead(_path);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream, Options)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void Write(Dictionary<string, string> pins)
    {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);

        var temp = Path.Combine(dir, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(pins, Options));
        File.Move(temp, _path, overwrite: true);
    }
}
