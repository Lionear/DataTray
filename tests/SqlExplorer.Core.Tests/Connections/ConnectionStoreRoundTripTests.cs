using SqlExplorer.Core.Connections;
using SqlExplorer.Infrastructure.Persistence;

namespace SqlExplorer.Core.Tests.Connections;

/// <summary>
/// The JSON store maps through its own DTO, so a property added to <see cref="SavedConnection"/> without
/// a matching DTO field is dropped on write — silently, and only visible after a restart. That is exactly
/// how SE-31's Favorite flag first shipped broken; this guards the round trip.
/// </summary>
public class ConnectionStoreRoundTripTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"conns-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    private SavedConnection Sample() => new()
    {
        Id = "c1",
        Name = "Conn",
        ProviderId = "postgres",
        Values = new Dictionary<string, string?> { ["host"] = "localhost" },
        Folder = "Klanten/Klant A",
        Color = "#E5484D",
        ReadOnly = true,
        SortOrder = 3,
        Favorite = true
    };

    [Fact]
    public void A_saved_connection_survives_a_reload_with_every_flag_intact()
    {
        new JsonConnectionStore(_path).Save(Sample());

        var reloaded = Assert.Single(new JsonConnectionStore(_path).GetAll());

        Assert.True(reloaded.Favorite);
        Assert.Equal("Klanten/Klant A", reloaded.Folder);
        Assert.Equal("#E5484D", reloaded.Color);
        Assert.True(reloaded.ReadOnly);
        Assert.Equal(3, reloaded.SortOrder);
        Assert.Equal("localhost", reloaded.Values["host"]);
    }

    [Fact]
    public void Unstarring_survives_a_reload_too()
    {
        var store = new JsonConnectionStore(_path);
        store.Save(Sample());

        store.Save(Sample() with { Favorite = false });

        Assert.False(Assert.Single(new JsonConnectionStore(_path).GetAll()).Favorite);
    }
}
