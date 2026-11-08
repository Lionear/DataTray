← [Plugins overview](../PLUGINS.md)

## Query panes (host API 9)

A standing-subsystem plugin can add a **pane** inside query tabs, for example a visual query builder. The
host owns the toggle, the column next to the editor and the pane's lifetime; the plugin supplies the control.

### Contracts

| Type | Purpose |
|---|---|
| `IQueryPanePlugin` | `PaneId`, `Title`, optional `Icon` and `Shortcut` (e.g. `"Ctrl+Shift+B"`), `AppliesTo(IQueryDocument)` and `CreatePane(IQueryPaneContext)`. |
| `IQueryPaneContext` | The tab's `Document`, `Schema`, `HostUi`, `Localizer`, persisted `State`, `GetServerVersionAsync`, dialect keywords/functions and `QuoteIdentifier`. |
| `IQuerySchema` | `GetTablesAsync`, `GetTableAsync` (columns, foreign keys) and a `Changed` event. Types: `QueryTableRef`, `QueryTable`, `QueryColumn`, `QueryForeignKey`. |
| `IQueryDocument` | `DocumentId`, `Kind` (`QueryDocumentKind`: Query, Browse, Monitor; handy in `AppliesTo`), `Sql`, `SelectedSql`, `SetSql`, `RunAsync`, `Connection`, `Database`, plus `IsReadOnly`, `IsLinked`, `Link`, `Unlink` and the events `ContextChanged`, `SqlChanged`, `Unlinked`. |

### Capabilities

Declare them in the plugin manifest; `ToolHostApi.Version` is 9 (`MinimumSupported` stays 1).

- `query-pane`: the plugin may contribute panes.
- `schema`: `IQueryPaneContext.Schema` is available. Without it the property is null.

### Lifecycle

- One pane is open per query tab at a time; opening another closes the first.
- Panes appear only in Query mode (not in Browse or Monitor tabs).
- `AppliesTo` is re-evaluated on `ContextChanged`.
- `CreatePane` runs once per open. If it throws, the tab stays usable, the pane column shows an error
  line and the exception is reported to Output.
- Closing the pane or the tab disposes the control. Implement `IDisposable` and unsubscribe from the document's
  events there; otherwise a closed pane keeps receiving them.
- `ContextChanged` fires once after a connection or database switch has settled, also when loading the
  database list failed.
- Plugin event handlers are isolated: an exception is reported to Output and never reaches other plugins
  or the host.
- `IQueryPaneContext.State` is an opaque string stored with the open tab and handed back after a restart.

### Linking the editor

A pane that generates SQL calls `Document.Link("My pane")` and then `SetSql(...)`. While linked:

- the editor is read-only (typing, paste, GUID insert, comment toggle, completion, Format, Replace, undo and redo are blocked)
  and a link bar shows who wrote the SQL, with **Copy** and **Edit as SQL**;
- **Edit as SQL**, closing the pane or closing the tab unlinks the editor and raises `Unlinked`;
- `SqlChanged` is raised for typing and for `SetSql`, so do not call `SetSql` from a `SqlChanged` handler
  unconditionally; it would loop.

### Nothing is blocked

DataTray never refuses a statement. Panes cannot veto Run, and the Run path does not classify or filter SQL.
