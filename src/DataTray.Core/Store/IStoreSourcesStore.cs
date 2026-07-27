namespace DataTray.Core.Store;

/// <summary>
/// Persists the <b>manually</b> added index URLs (<c>store-sources.json</c>). The Discovery feed is not
/// stored here — it is fetched fresh and is not user-editable. The catalog merges Discovery's index URLs
/// with these manual ones.
/// </summary>
public interface IStoreSourcesStore
{
    IReadOnlyList<string> GetManualSources();

    /// <summary>Adds a manual index URL if not already present (case-insensitive, order-preserving).</summary>
    void AddManualSource(string indexUrl);

    void RemoveManualSource(string indexUrl);
}
