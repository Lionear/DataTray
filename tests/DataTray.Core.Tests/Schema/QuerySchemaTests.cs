using DataTray.Core.Connections;
using DataTray.Core.Schema;
using DataTray.Sdk.Schema;
using DataTray.Sdk.Extensibility;

namespace DataTray.Core.Tests.Schema;

public class QuerySchemaTests
{
    private static readonly SavedConnection Conn = new()
    {
        Id = "c1",
        Name = "c1",
        ProviderId = "postgres",
        Values = new Dictionary<string, string?>(),
    };

    private static SchemaObject Obj(string? db, string name, DbNodeKind kind = DbNodeKind.Table) =>
        new() { Kind = kind, Database = db, Schema = "dbo", Name = name };

    private static QuerySchema Create(FakeCache cache, string? database) =>
        new(cache, () => Conn, () => database);

    [Fact]
    public async Task GetTables_FiltersOnTheTabDatabase()
    {
        var cache = new FakeCache { Snapshot = new([Obj("Sales", "Orders"), Obj("master", "spt"), Obj("Sales", "V", DbNodeKind.View)]) };

        var tables = await Create(cache, "Sales").GetTablesAsync();

        Assert.Equal([new QueryTableRef("Sales", "dbo", "Orders", false), new QueryTableRef("Sales", "dbo", "V", true)], tables);
    }

    [Fact]
    public async Task GetTables_NullDatabase_ReturnsEverything()
    {
        var cache = new FakeCache { Snapshot = new([Obj(null, "A"), Obj(null, "B")]) };

        var tables = await Create(cache, null).GetTablesAsync();

        Assert.Equal(2, tables.Count);
    }

    [Fact]
    public async Task GetTables_NoConnection_ReturnsEmpty()
    {
        var schema = new QuerySchema(new FakeCache(), () => null, () => null);

        Assert.Empty(await schema.GetTablesAsync());
    }

    [Fact]
    public async Task GetTables_SnapshotMissing_BuildsOnce()
    {
        var cache = new FakeCache { BuiltSnapshot = new([Obj("Sales", "Orders")]) };
        var schema = Create(cache, "Sales");

        var first = await schema.GetTablesAsync();
        await schema.GetTablesAsync();

        Assert.Equal(1, cache.BuildCount);
        Assert.Single(first);
    }

    [Fact]
    public async Task GetTable_ReturnsColumnsAndForeignKeys()
    {
        var orders = Obj("Sales", "Orders") with
        {
            Columns = [new SchemaColumn("id", "int"), new SchemaColumn("customer_id", null)],
            ForeignKeys = [new SchemaForeignKey("customer_id", "Customers", "id")],
        };
        var cache = new FakeCache { Snapshot = new([orders]) };

        var table = await Create(cache, "Sales").GetTableAsync(new QueryTableRef("Sales", "dbo", "Orders", false));

        Assert.Equal([new QueryColumn("id", "int"), new QueryColumn("customer_id", null)], table!.Columns);
        Assert.Equal([new QueryForeignKey("customer_id", "Customers", "id")], table.ForeignKeys);
    }

    [Fact]
    public async Task GetTable_Unknown_ReturnsNull()
    {
        var cache = new FakeCache { Snapshot = new([]) };

        Assert.Null(await Create(cache, null).GetTableAsync(new QueryTableRef(null, null, "Nope", false)));
    }

    [Fact]
    public void Changed_ForwardsCacheChanged_AndStopsAfterDispose()
    {
        var cache = new FakeCache();
        var schema = Create(cache, null);
        var raised = new List<object?>();
        schema.Changed += (s, _) => raised.Add(s);

        cache.Raise();
        schema.Dispose();
        cache.Raise();

        Assert.Equal([schema], raised);
    }

    private sealed class FakeCache : ISchemaCache
    {
        public SchemaSnapshot? Snapshot { get; set; }
        public SchemaSnapshot? BuiltSnapshot { get; set; }
        public int BuildCount { get; private set; }

        public event Action? Changed;

        public SchemaSnapshot? Get(string connectionId) => Snapshot;

        public Task BuildAsync(SavedConnection connection, CancellationToken ct = default)
        {
            BuildCount++;
            Snapshot = BuiltSnapshot;
            return Task.CompletedTask;
        }

        public void Invalidate(string connectionId) { }

        public void Raise() => Changed?.Invoke();
    }
}
