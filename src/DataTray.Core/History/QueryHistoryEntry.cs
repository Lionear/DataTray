namespace DataTray.Core.History;

/// <summary>What produced a history entry — a typed query run or an editable-grid save.</summary>
public enum QueryHistoryKind
{
    Query,
    Save
}

/// <summary>Who issued a history entry. <see cref="User"/> is the default so entries written before the
/// MCP server existed keep their meaning; <see cref="Ai"/> marks a query run by an AI client over MCP.</summary>
public enum QueryHistorySource
{
    User,
    Ai
}

/// <summary>
/// One executed statement kept in the query history so it can be searched and re-run. Holds only the
/// SQL and outcome metadata — never result data or secrets. Browse paging is deliberately not logged.
/// </summary>
public sealed record QueryHistoryEntry
{
    public required string Id { get; init; }
    public required DateTime TimestampUtc { get; init; }
    public required string ConnectionId { get; init; }
    public required string ConnectionName { get; init; }
    public required QueryHistoryKind Kind { get; init; }
    public required string Sql { get; init; }
    public long DurationMs { get; init; }
    public int RowCount { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }

    /// <summary>Who issued this entry. Absent/default = <see cref="QueryHistorySource.User"/> so pre-MCP
    /// history stays correct; the MCP server stamps <see cref="QueryHistorySource.Ai"/> on its runs.</summary>
    public QueryHistorySource Source { get; init; } = QueryHistorySource.User;
}
