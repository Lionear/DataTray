namespace SqlExplorer.Core.History;

/// <summary>
/// A query kept for later (SE-31). Starred from the history panel, but stored separately: history is a
/// capped ring buffer that <see cref="IQueryHistoryStore.Clear"/> empties, so a favorite living there
/// would quietly disappear. The SQL is copied, not referenced, for the same reason.
/// </summary>
public sealed record FavoriteQuery
{
    public required string Id { get; init; }

    public required string Sql { get; init; }

    public required DateTime CreatedUtc { get; init; }

    /// <summary>The connection it was starred from, for context in the list. The query can be run
    /// anywhere; this is a label, not a binding.</summary>
    public string? ConnectionName { get; init; }

    /// <summary>Optional name given by the user; the SQL itself is shown when absent.</summary>
    public string? Title { get; init; }
}
