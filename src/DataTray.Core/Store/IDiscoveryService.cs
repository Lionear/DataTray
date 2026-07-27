namespace DataTray.Core.Store;

/// <summary>
/// Fetches the fixed Lionear Discovery feed (a registry of stores). Fault-tolerant: when the feed can't
/// be reached it falls back to the last successfully fetched list rather than failing the catalog, so a
/// transient outage doesn't empty the Store.
/// </summary>
public interface IDiscoveryService
{
    Task<IReadOnlyList<DiscoveryEntry>> GetStoresAsync(CancellationToken ct);
}
