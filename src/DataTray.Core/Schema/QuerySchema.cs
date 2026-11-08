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
    private (string Id, Task Task)? _build;

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
        var connection = _connection();
        var database = _database();
        var objects = await LoadObjectsAsync(connection, ct);
        return objects
            .Where(o => database is null || o.Database == database)
            .Select(ToRef)
            .ToList();
    }

    public async Task<QueryTable?> GetTableAsync(QueryTableRef table, CancellationToken ct = default)
    {
        var objects = await LoadObjectsAsync(_connection(), ct);
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

    private async Task<IReadOnlyList<SchemaObject>> LoadObjectsAsync(SavedConnection? connection, CancellationToken ct)
    {
        if (connection is null)
        {
            return [];
        }

        if (_cache.Get(connection.Id) is null)
        {
            // Share only an in-flight build for this connection; finished (even failed) builds are redone.
            // Not cancelled per caller: another caller may await the same build.
            if (_build is not { } b || b.Id != connection.Id || b.Task.IsCompleted)
            {
                _build = (connection.Id, _cache.BuildAsync(connection, CancellationToken.None));
            }

            await _build.Value.Task.WaitAsync(ct);
        }

        return _cache.Get(connection.Id)?.Objects ?? [];
    }
}
