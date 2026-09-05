namespace DataTray.Sdk.Extensibility;

/// <summary>What an open document tab is: a free SQL pane, a paged table browser, or an activity monitor.</summary>
public enum QueryDocumentKind
{
    Query,
    Browse,
    Monitor,
}

/// <summary>
/// One open query window, as a query-toolbar contribution sees it. The surface is deliberately small —
/// read the SQL, rewrite the SQL, run it — which covers a formatter, a linter, a snippet inserter or a
/// "explain this query" assistant without exposing the result grid, the paging state or the pending edit
/// buffer. Those stay out until a plugin needs them and the shape is clear.
/// </summary>
/// <remarks>
/// <see cref="Connection"/> is a <see cref="ManagedConnectionInfo"/>, not a connection profile: that
/// carries the connection string with credentials in it, and a generic toolbar extension has no business
/// holding a secret it does not need in order to connect. Providers keep getting the full profile through
/// their own seams, because connecting is exactly what they do.
/// </remarks>
public interface IQueryDocument
{
    /// <summary>Stable for the lifetime of the tab.</summary>
    string DocumentId { get; }

    QueryDocumentKind Kind { get; }

    /// <summary>The connection this tab runs against — non-secret values only, the same DTO
    /// <see cref="IManagedConnections"/> hands out. Null while the tab has no connection.</summary>
    ManagedConnectionInfo? Connection { get; }

    /// <summary>The database/catalog picked in the tab's switcher, when the engine has one.</summary>
    string? Database { get; }

    /// <summary>Snapshot of the editor text. Not live — read it inside your action, don't cache it.</summary>
    string Sql { get; }

    /// <summary>Snapshot of the current selection, or null when nothing is selected.</summary>
    string? SelectedSql { get; }

    /// <summary>Replace the editor text (undoable, as if typed).</summary>
    void SetSql(string sql);

    /// <summary>Run the tab exactly as the Run button does; completes when the run finishes.</summary>
    Task RunAsync(CancellationToken ct = default);
}
