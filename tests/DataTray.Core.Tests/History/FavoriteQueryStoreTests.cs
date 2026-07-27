using DataTray.Core.History;
using DataTray.Infrastructure.Persistence;

namespace DataTray.Core.Tests.History;

/// <summary>
/// SE-31: starred queries live in their own store precisely so they outlive the history ring buffer and
/// Clear history. These guard that separation and the de-duplication the star relies on.
/// </summary>
public class FavoriteQueryStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"favq-{Guid.NewGuid():N}.json");

    private JsonFavoriteQueryStore NewStore() => new(_path);

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    [Fact]
    public void Add_then_FindBySql_returns_the_favorite()
    {
        var store = NewStore();

        store.Add("SELECT 1", "conn");

        Assert.NotNull(store.FindBySql("SELECT 1"));
    }

    [Fact]
    public void Add_is_idempotent_for_the_same_sql()
    {
        var store = NewStore();

        var first = store.Add("SELECT 1", "conn");
        var second = store.Add("SELECT 1", "other");

        Assert.Equal(first.Id, second.Id);
        Assert.Single(store.GetAll());
    }

    [Fact]
    public void Add_ignores_surrounding_whitespace_when_matching()
    {
        var store = NewStore();
        store.Add("SELECT 1", "conn");

        store.Add("  SELECT 1\n", "conn");

        Assert.Single(store.GetAll());
    }

    [Fact]
    public void Remove_drops_it()
    {
        var store = NewStore();
        var favorite = store.Add("SELECT 1", "conn");

        store.Remove(favorite.Id);

        Assert.Empty(store.GetAll());
        Assert.Null(store.FindBySql("SELECT 1"));
    }

    [Fact]
    public void Favorites_survive_a_history_clear()
    {
        // The whole reason for a separate store: clearing history must not touch what was starred.
        var history = new JsonQueryHistoryStore(_path + ".history");
        var store = NewStore();
        history.Append(new QueryHistoryEntry
        {
            Id = "1",
            TimestampUtc = DateTime.UtcNow,
            ConnectionId = "c1",
            ConnectionName = "conn",
            Kind = QueryHistoryKind.Query,
            Sql = "SELECT 1"
        });
        store.Add("SELECT 1", "conn");

        history.Clear();

        Assert.Empty(history.GetRecent(10));
        Assert.Single(store.GetAll());
        File.Delete(_path + ".history");
    }

    [Fact]
    public void A_reopened_store_reads_what_was_written()
    {
        NewStore().Add("SELECT 1", "conn");

        Assert.Single(NewStore().GetAll());
    }

    [Fact]
    public void GetAll_is_newest_first()
    {
        var store = NewStore();
        store.Add("SELECT 1", "conn");
        store.Add("SELECT 2", "conn");

        Assert.Equal("SELECT 2", store.GetAll()[0].Sql);
    }
}
