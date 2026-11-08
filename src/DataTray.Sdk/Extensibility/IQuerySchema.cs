namespace DataTray.Sdk.Extensibility;

/// <summary>Read-only view of the tables and views of a query tab's connection/database.</summary>
public interface IQuerySchema
{
    /// <summary>Lists the tables and views.</summary>
    Task<IReadOnlyList<QueryTableRef>> GetTablesAsync(CancellationToken ct = default);

    /// <summary>Loads columns and foreign keys of one table; null when it no longer exists.</summary>
    Task<QueryTable?> GetTableAsync(QueryTableRef table, CancellationToken ct = default);

    /// <summary>Raised when the schema changed or was reloaded.</summary>
    event EventHandler? Changed;
}
