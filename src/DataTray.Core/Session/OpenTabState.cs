namespace DataTray.Core.Session;

/// <summary>One restored query tab (browse tabs reopen from the tree): connection, database, SQL and, when file-backed
/// (SE-154), the <c>.sql</c> path. <see cref="IsActive"/> marks the tab selected at close (SE-179); <see cref="PaneStates"/>
/// holds opaque per-plugin pane state (SE-299). Those three are optional, so an older open-tabs.json still loads.</summary>
public sealed record OpenTabState(string ConnectionId, string? Database, string Sql, string? FilePath = null, bool IsActive = false,
    IReadOnlyDictionary<string, string>? PaneStates = null);
