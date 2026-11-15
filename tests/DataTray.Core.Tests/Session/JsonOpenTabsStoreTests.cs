using DataTray.Core.Session;
using DataTray.Infrastructure.Persistence;

namespace DataTray.Core.Tests.Session;

public sealed class JsonOpenTabsStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("open-tabs-").FullName;
    private string FilePath => Path.Combine(_dir, "open-tabs.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static OpenTabState Tab(string sql) => new("c1", "db", sql, IsActive: true);

    [Fact]
    public void Save_ThenLoad_RoundTripsUnsavedText()
    {
        var store = new JsonOpenTabsStore(FilePath);

        store.Save([Tab("select 1"), Tab("select 2")]);

        Assert.Equal(["select 1", "select 2"], new JsonOpenTabsStore(FilePath).Load().Select(t => t.Sql));
    }

    [Fact]
    public void Save_LeavesNoTempFileBehind()
    {
        new JsonOpenTabsStore(FilePath).Save([Tab("select 1")]);

        Assert.Equal([FilePath], Directory.GetFiles(_dir));
    }

    // Autosave calls Save on a timer; an unchanged session must cost no disk write at all (SE-301).
    [Fact]
    public void Save_WithAnUnchangedSession_DoesNotWriteAgain()
    {
        var store = new JsonOpenTabsStore(FilePath);
        store.Save([Tab("select 1")]);
        var stamp = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(FilePath, stamp);

        store.Save([Tab("select 1")]);

        Assert.Equal(stamp, File.GetLastWriteTimeUtc(FilePath));
    }

    [Fact]
    public void Save_WithAChangedSession_Writes()
    {
        var store = new JsonOpenTabsStore(FilePath);
        store.Save([Tab("select 1")]);

        store.Save([Tab("select 1 -- edited")]);

        Assert.Equal("select 1 -- edited", store.Load().Single().Sql);
    }
}
