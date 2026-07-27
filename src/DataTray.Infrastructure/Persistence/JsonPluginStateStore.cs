using System.Text.Json;
using System.Text.Json.Serialization;
using DataTray.Core;
using DataTray.Core.Plugins;

namespace DataTray.Infrastructure.Persistence;

/// <summary>
/// Stores per-plugin enable/pending state as a single JSON file next to connections.json, shaped
/// <c>{ "&lt;pluginId&gt;": { "enabled": true, "pending": "install" } }</c>. Writes are atomic
/// (temp file + replace) and a corrupt/unreadable file degrades to empty rather than crashing —
/// mirrors <see cref="JsonPluginSettingsStore"/>. Enums serialise as their camelCase name so the file
/// stays human-readable and stable across enum-member reordering.
/// </summary>
public sealed class JsonPluginStateStore : IPluginStateStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _path;

    public JsonPluginStateStore(string? path = null)
    {
        _path = path ?? DefaultPath();
    }

    private static string DefaultPath()
    {
        var dir = AppPaths.Root;
        return Path.Combine(dir, "plugins-state.json");
    }

    public IReadOnlyDictionary<string, PluginStateEntry> GetAll() => ReadAll();

    public PluginStateEntry Get(string id) =>
        ReadAll().TryGetValue(id, out var entry) ? entry : new PluginStateEntry();

    public void Save(string id, PluginStateEntry entry)
    {
        var all = ReadAll();
        all[id] = entry;
        Write(all);
    }

    public void Remove(string id)
    {
        var all = ReadAll();
        if (all.Remove(id))
        {
            Write(all);
        }
    }

    private Dictionary<string, PluginStateEntry> ReadAll()
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, PluginStateEntry>(StringComparer.Ordinal);
        }

        try
        {
            using var stream = File.OpenRead(_path);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, PluginStateEntry>>(stream, Options);
            return parsed is null
                ? new Dictionary<string, PluginStateEntry>(StringComparer.Ordinal)
                : new Dictionary<string, PluginStateEntry>(parsed, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new Dictionary<string, PluginStateEntry>(StringComparer.Ordinal);
        }
    }

    private void Write(Dictionary<string, PluginStateEntry> all)
    {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);

        var temp = Path.Combine(dir, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(all, Options));
        File.Move(temp, _path, overwrite: true);
    }
}
