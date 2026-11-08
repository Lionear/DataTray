using DataTray.Core.Connections;
using DataTray.Sdk.Extensibility;
using DataTray.Sdk.Schema;

namespace DataTray.Core.Schema;

/// <summary>Read-only <see cref="IQuerySchema"/> over the schema cache, scoped to a tab's connection/database.</summary>
public sealed class QuerySchema : IQuerySchema, IDisposable
{
    private readonly ISchemaCache _cache;
    private readonly Func<SavedConnection?> _connection;
    private readonly Func<string?> _database;
    private Task? _build;

    public QuerySchema(ISchemaCache cache, Func<SavedConnection?> connection, Func<string?> database)
    {
        _cache = cache;
        _connection = connection;
        _database = database;
        _cache.Changed += OnCacheChanged;
    }

    public event EventHandler? Changed;

    public async Task<IReadOnlyList<QueryTableRef>> GetTablesAsync(CancellationToken ct = default)
    {
        var database = _database();
        var objects = await LoadObjectsAsync(ct);
        return objects
            .Where(o => database is null || o.Database == database)
            .Select(ToRef)
            .ToList();
    }

    public async Task<QueryTable?> GetTableAsync(QueryTableRef table, CancellationToken ct = default)
    {
        var objects = await LoadObjectsAsync(ct);
        var found = objects.FirstOrDefault(o =>
            o.Database == table.Database && o.Schema == table.Schema && o.Name == table.Name);
        if (found is null)
        {
            return null;
        }

        return new QueryTable(
            ToRef(found),
            found.Columns.Select(c => new QueryColumn(c.Name, c.Type)).ToList(),
            found.ForeignKeys.Select(f => new QueryForeignKey(f.Column, f.ReferencedTable, f.ReferencedColumn)).ToList());
    }

    public void Dispose()
    {
        _cache.Changed -= OnCacheChanged;
    }

    private static QueryTableRef ToRef(SchemaObject o) =>
        new(o.Database, o.Schema, o.Name, o.Kind == DbNodeKind.View);

    private void OnCacheChanged()
    {
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<IReadOnlyList<SchemaObject>> LoadObjectsAsync(CancellationToken ct)
    {
        var connection = _connection();
        if (connection is null)
        {
            return [];
        }

        if (_cache.Get(connection.Id) is null)
        {
            // One build per instance, shared by racing callers. Not cancelled per caller: another may await it.
            _build ??= _cache.BuildAsync(connection, CancellationToken.None);
            await _build.WaitAsync(ct);
        }

        return _cache.Get(connection.Id)?.Objects ?? [];
    }
}
