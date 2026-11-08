namespace DataTray.Sdk.Extensibility;

/// <summary>The tree node a node-menu item was invoked on, plus the active query tab (if any).</summary>
public sealed record NodeMenuTarget(
    ManagedConnectionInfo Connection,
    string? Database,
    string? Schema,
    string Name,
    bool IsView,
    IQueryDocument? ActiveDocument);
