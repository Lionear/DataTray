using System.Text.Json;
using DataTray.Core;
using DataTray.Core.Toolbar;

namespace DataTray.Infrastructure.Persistence;

/// <summary>
/// Persists the toolbar arrangement as toolbar.json under the user's config dir, beside keymap.json.
/// Same atomic write (temp file + replace) and degrade-to-empty idiom as <see cref="JsonKeymapStore"/>:
/// an install that never touched the toolbar has no file, and an empty/unreadable one falls back to the
/// catalog defaults rather than blocking startup.
/// </summary>
public sealed class JsonToolbarLayoutStore : IToolbarLayoutStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;

    public JsonToolbarLayoutStore(string? path = null)
    {
        _path = path ?? Path.Combine(AppPaths.Root, "toolbar.json");
    }

    public IReadOnlyList<ToolbarLayoutItem> Load()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            using var stream = File.OpenRead(_path);
            return JsonSerializer.Deserialize<List<ToolbarLayoutItem>>(stream, Options) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return [];
        }
    }

    public void Save(IReadOnlyList<ToolbarLayoutItem> layout)
    {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);

        var temp = Path.Combine(dir, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(layout, Options));
        File.Move(temp, _path, overwrite: true);
    }
}
