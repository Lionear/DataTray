namespace DataTray.Core.Session;

/// <summary>One restored query tab: its connection, selected database, the SQL it held, and — when the
/// tab was backed by a file (SE-154) — the <c>.sql</c> file path so the association returns on the next
/// launch. <see cref="IsActive"/> marks the tab that was selected at close, so it is reselected on restore
/// (SE-179). Browse tabs aren't persisted — they reopen from the tree. <see cref="FilePath"/>/<see cref="IsActive"/>
/// are optional and default to null/false, so an open-tabs.json written before those fields still loads.</summary>
public sealed record OpenTabState(string ConnectionId, string? Database, string Sql, string? FilePath = null, bool IsActive = false);
