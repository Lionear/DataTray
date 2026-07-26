namespace SqlExplorer.Core.History;

/// <summary>Persists starred queries independently of the history ring buffer (SE-31).</summary>
public interface IFavoriteQueryStore
{
    /// <summary>Raised after the favorites change so an open panel can refresh.</summary>
    event Action? Changed;

    /// <summary>Newest first.</summary>
    IReadOnlyList<FavoriteQuery> GetAll();

    /// <summary>Star a query. Starring SQL that is already a favorite is a no-op, so the same entry can
    /// be clicked twice without collecting duplicates.</summary>
    FavoriteQuery Add(string sql, string? connectionName, string? title = null);

    void Remove(string id);

    /// <summary>The favorite holding exactly this SQL, or null — drives the star's on/off state in the
    /// history list, where entries are identified by their text rather than by id.</summary>
    FavoriteQuery? FindBySql(string sql);
}
