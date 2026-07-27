namespace DataTray.Core.Store;

/// <summary>A plugin in the merged catalog, tagged with the source it came from (provenance the UI shows,
/// relevant once several sources are merged).</summary>
public sealed record CatalogEntry(StoreEntry Entry, string SourceUrl, string? SourceName);

/// <summary>A bundle in the merged catalog, tagged with its source.</summary>
public sealed record CatalogBundle(StoreBundle Bundle, string SourceUrl, string? SourceName);

/// <summary>How one source fared during a fetch — drives the Sources tab's per-source status and the
/// offline/error empty-states. <see cref="IconUrl"/> is the store's icon (Discovery sources only).</summary>
public sealed record SourceStatus(string Url, string? Name, bool IsDiscovery, bool Ok, string? Error, string? IconUrl = null);

/// <summary>
/// The merged result of fetching every source (Discovery ∪ manual). Deduped by id — the first source
/// (Discovery order, then manual order) that lists an id wins, but the losing sources still contribute
/// their <see cref="Sources"/> status. Fault-tolerant: a source that fails is recorded in
/// <see cref="Sources"/> and skipped, it never aborts the whole fetch.
/// </summary>
public sealed record StoreCatalog(
    IReadOnlyList<CatalogEntry> Entries,
    IReadOnlyList<CatalogBundle> Bundles,
    IReadOnlyList<SourceStatus> Sources);

/// <summary>Fetches and merges the store catalog across all active sources.</summary>
public interface IStoreCatalog
{
    Task<StoreCatalog> FetchAsync(CancellationToken ct);
}
