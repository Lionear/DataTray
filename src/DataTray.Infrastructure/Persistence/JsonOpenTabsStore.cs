using System.Text.Json;
using DataTray.Core;
using DataTray.Core.Session;

namespace DataTray.Infrastructure.Persistence;

/// <summary>
/// Persists the open query tabs as open-tabs.json under the user's config dir, beside settings.json.
/// Same atomic write + degrade-to-empty idiom as the other JSON stores: a missing or malformed file
/// just means "no session to restore".
/// </summary>
public sealed class JsonOpenTabsStore : IOpenTabsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;

    // What the file holds after our last write. Autosave calls Save on a timer; an unchanged session
    // costs a string compare instead of a disk write.
    private string? _lastWritten;

    public JsonOpenTabsStore(string? path = null)
    {
        _path = path ?? DefaultPath();
    }

    private static string DefaultPath()
    {
        var dir = AppPaths.Root;
        return Path.Combine(dir, "open-tabs.json");
    }

    public IReadOnlyList<OpenTabState> Load()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            using var stream = File.OpenRead(_path);
            return JsonSerializer.Deserialize<List<OpenTabState>>(stream, Options) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A malformed or briefly-locked session file must never block startup.
            return [];
        }
    }

    public void Save(IReadOnlyList<OpenTabState> tabs)
    {
        var json = JsonSerializer.Serialize(tabs, Options);
        if (json == _lastWritten)
        {
            return;
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(_path)) ?? Path.GetPathRoot(_path) ?? ".";
        Directory.CreateDirectory(dir);

        // Temp file + replace: an interrupted write leaves the previous session intact, never half a file.
        var temp = Path.Combine(dir, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, json);
        File.Move(temp, _path, overwrite: true);
        _lastWritten = json;
    }
}
