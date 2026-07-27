using System.Text.Json;
using System.Text.Json.Serialization;
using DataTray.Core;
using DataTray.Core.History;

namespace DataTray.Infrastructure.Persistence;

/// <summary>
/// Starred queries in <c>favorite-queries.json</c> beside history.json, following the same rules as
/// <see cref="JsonQueryHistoryStore"/>: atomic writes (temp + replace), a corrupt or unreadable file
/// degrades to empty rather than crashing, and entries are cached in memory after the first load.
/// Unlike history there is no cap — a favorite is there because someone asked for it.
/// </summary>
public sealed class JsonFavoriteQueryStore : IFavoriteQueryStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly object _gate = new();
    private List<FavoriteQuery>? _entries; // oldest first; null until loaded

    public event Action? Changed;

    public JsonFavoriteQueryStore(string? path = null)
    {
        _path = path ?? DefaultPath();
    }

    private static string DefaultPath()
    {
        var dir = AppPaths.Root;
        return Path.Combine(dir, "favorite-queries.json");
    }

    public IReadOnlyList<FavoriteQuery> GetAll()
    {
        lock (_gate)
        {
            return Enumerable.Reverse(Load()).ToList();
        }
    }

    public FavoriteQuery Add(string sql, string? connectionName, string? title = null)
    {
        FavoriteQuery favorite;
        lock (_gate)
        {
            var entries = Load();
            if (Match(entries, sql) is { } existing)
            {
                return existing;
            }

            favorite = new FavoriteQuery
            {
                Id = Guid.NewGuid().ToString("N"),
                Sql = sql,
                ConnectionName = connectionName,
                Title = title,
                CreatedUtc = DateTime.UtcNow
            };

            entries.Add(favorite);
            Write(entries);
        }

        Changed?.Invoke();
        return favorite;
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            var entries = Load();
            if (entries.RemoveAll(e => e.Id == id) == 0)
            {
                return;
            }

            Write(entries);
        }

        Changed?.Invoke();
    }

    public FavoriteQuery? FindBySql(string sql)
    {
        lock (_gate)
        {
            return Match(Load(), sql);
        }
    }

    // Compared on the trimmed text: the same query re-run from the editor differs by trailing whitespace
    // often enough that an exact match would let duplicates through.
    private static FavoriteQuery? Match(List<FavoriteQuery> entries, string sql) =>
        entries.FirstOrDefault(e => string.Equals(e.Sql.Trim(), sql.Trim(), StringComparison.Ordinal));

    private List<FavoriteQuery> Load()
    {
        if (_entries is not null)
        {
            return _entries;
        }

        if (!File.Exists(_path))
        {
            return _entries = [];
        }

        try
        {
            using var stream = File.OpenRead(_path);
            return _entries = JsonSerializer.Deserialize<List<FavoriteQuery>>(stream, Options) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return _entries = [];
        }
    }

    private void Write(List<FavoriteQuery> entries)
    {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);

        var temp = Path.Combine(dir, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(entries, Options));
        File.Move(temp, _path, overwrite: true);
    }
}
