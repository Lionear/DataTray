using System.Text.Json;
using DataTray.Core.Store;

namespace DataTray.Infrastructure.Store;

/// <summary>
/// Stores the manually added index URLs as a JSON string array (<c>store-sources.json</c>) next to
/// connections.json. Writes are atomic (temp file + replace) and a corrupt/unreadable file degrades to
/// empty rather than crashing — mirrors the other JSON stores. Adds are case-insensitive and preserve
/// insertion order.
/// </summary>
public sealed class JsonStoreSourcesStore : IStoreSourcesStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true
    };

    private readonly string _path;

    public JsonStoreSourcesStore(string? path = null)
    {
        _path = path ?? DefaultPath();
    }

    private static string DefaultPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Lionear", "SqlExplorer");
        return Path.Combine(dir, "store-sources.json");
    }

    public IReadOnlyList<string> GetManualSources() => Read();

    public void AddManualSource(string indexUrl)
    {
        var url = indexUrl.Trim();
        if (url.Length == 0)
        {
            return;
        }

        var all = Read();
        if (all.Any(s => string.Equals(s, url, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        Write([.. all, url]);
    }

    public void RemoveManualSource(string indexUrl)
    {
        var all = Read();
        var remaining = all.Where(s => !string.Equals(s, indexUrl, StringComparison.OrdinalIgnoreCase)).ToList();
        if (remaining.Count != all.Count)
        {
            Write(remaining);
        }
    }

    private List<string> Read()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            using var stream = File.OpenRead(_path);
            return JsonSerializer.Deserialize<List<string>>(stream, Options) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return [];
        }
    }

    private void Write(List<string> sources)
    {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);

        var temp = Path.Combine(dir, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(sources, Options));
        File.Move(temp, _path, overwrite: true);
    }
}
