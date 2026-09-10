namespace DataTray.Core.Toolbar;

/// <summary>One row of the user's toolbar layout: which action, and whether it is shown. Order is the
/// position in the list it lives in.</summary>
public sealed record ToolbarLayoutItem(string Id, bool Visible);
