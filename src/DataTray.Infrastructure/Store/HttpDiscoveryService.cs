using DataTray.Core.Store;

namespace DataTray.Infrastructure.Store;

/// <summary>
/// Fetches the fixed Lionear Discovery feed over HTTP. The URL is a hardcoded constant — deliberately not
/// a setting, since Discovery is the non-configurable registry of stores. Keeps the last successful list
/// in memory and returns it if a later fetch fails (offline/5xx), so a transient outage never empties the
/// catalog. A first-ever fetch that fails yields an empty list, not an exception.
/// </summary>
public sealed class HttpDiscoveryService(HttpClient http, string? discoveryUrl = null) : IDiscoveryService
{
    /// <summary>The canonical Discovery feed, on the dedicated plugin host under this product's path
    /// (so sibling products get their own <c>plugins.lionear.dev/&lt;product&gt;/</c> space).</summary>
    public const string DefaultDiscoveryUrl = "https://plugins.lionear.dev/sql-explorer/discovery.json";

    private readonly string _url = discoveryUrl ?? DefaultDiscoveryUrl;
    private IReadOnlyList<DiscoveryEntry> _lastGood = [];

    public async Task<IReadOnlyList<DiscoveryEntry>> GetStoresAsync(CancellationToken ct)
    {
        try
        {
            var json = await http.GetStringAsync(_url, ct);
            _lastGood = DiscoveryDocument.Parse(json).Stores;
            return _lastGood;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or FormatException)
        {
            // Offline or a bad payload: fall back to the last good list (empty on a first-ever failure).
            return _lastGood;
        }
    }
}
