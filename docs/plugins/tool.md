← [Plugins overview](../PLUGINS.md)

## Plugin type: `tool`

A **tool** contributes an action rather than a database engine: it shows up as a
menu item on the schema tree, collects some inputs in a dialog, and runs against
the selected connection/node. The Universal Backup & Restore feature is itself a
tool plugin. Tools reference the same `DataTray.Sdk` assembly as
providers and are staged into `plugins/` the same way.

### The contract: `IToolPlugin`

```csharp
public interface IToolPlugin
{
    string Id { get; }                       // stable; one assembly may ship several tools
    string Title { get; }                    // menu-item / dialog title, e.g. "Backup…"
    ProviderIcon? Icon => null;
    ToolTarget Target { get; }               // where in the tree the tool is offered
    IReadOnlyList<ToolField> Fields { get; } // Route A: the inputs the host renders

    bool IsDestructive => false;             // true → host shows a confirmation first (e.g. restore)

    Task<string?> PreviewAsync(string filePath, CancellationToken ct) => Task.FromResult<string?>(null);

    Task ExecuteAsync(
        ToolExecutionContext context,
        IReadOnlyDictionary<string, string?> inputs,
        IProgress<ToolProgress> progress,
        CancellationToken ct);
}
```

| Member | Purpose |
|---|---|
| `Id` | Stable id. One tool assembly may contain several `IToolPlugin` classes — all are loaded — so this need not match the manifest `id`. |
| `Title` | Menu-item and dialog title. |
| `Target` | A `ToolTarget` that decides which tree nodes offer the tool (see below). |
| `Fields` | The Route A input declarations; the host renders a generic dialog from them, exactly like the connection form. Empty when the tool uses a Route B custom view. |
| `IsDestructive` | When true the host shows a destructive-action confirmation before running. |
| `IsActivityMonitor` | Declares this tool *is* the connection's Activity Monitor (an engine-specific replacement for the host's built-in one). The host's own "Activity Monitor…" item on a connection root opens it, and it is left out of the node's `Tools` submenu — a tool that **replaces** a feature keeps that feature's place, unlike one that adds a feature. |
| `PreviewAsync` | Optional: when a `File` field changes, return a short summary of the chosen file (e.g. read a backup header) shown under that field before Execute runs. |
| `ExecuteAsync` | Runs the tool. `inputs` holds the collected field values keyed by `ToolField.Key`; report progress lines through `progress`. |

### Where the tool is offered: `ToolTarget`

```csharp
public sealed record ToolTarget(
    IReadOnlyList<string>? ProviderIds = null,   // null = every provider (the "universal" case)
    IReadOnlyList<DbNodeKind>? NodeKinds = null,  // null = any node kind
    bool IncludeConnectionRoot = false);          // the connection root has no node kind
```

The host shows the tool on a node only when the node's provider is in
`ProviderIds` **and** its kind is in `NodeKinds`. Because the connection root has
no node kind, a whole-connection tool sets `IncludeConnectionRoot = true` rather
than trying to express the root via `NodeKinds`.

### What a tool receives at run time: `ToolExecutionContext`

```csharp
public sealed record ToolExecutionContext(
    ConnectionProfile Profile,   // includes the resolved ConnectionString (secrets already fetched)
    DbNodeRef? Node,             // the node the tool launched on; null at the connection root
    IDbProvider Provider,        // walk schema / run queries through the same interface the host uses
    string ProviderId,
    IToolHost Host,              // host-only services: file pickers + GetPluginSetting(key)
    IReadOnlyList<DbNodeRef>? NodePath = null);  // root → Node, inclusive (v7)
```

The `Provider` handed over is the live provider for that connection, so a generic
("universal") tool can introspect the schema, run queries and recreate objects
through the same `IDbProvider` the host uses — no driver dependency of its own.

`NodePath` is the ancestry from the connection root down to `Node`, inclusive —
the same list a provider receives for introspection. A name alone does not
identify every node: an index is named within its table, and an "Indexes" folder
is called that under every table in the database. Ask for an ancestor by kind
rather than walking the list:

```csharp
var table = context.Ancestor(DbNodeKind.Table);   // null when there is none
var schema = context.Ancestor(DbNodeKind.Schema);
```

It is empty on a host older than tool API 7, so a tool that needs it should say
what is missing rather than guess.

### The `ToolField` form (Route A)

```csharp
public sealed record ToolField(
    string Key, string Label,
    ToolFieldType Type = ToolFieldType.Text,   // Text | Password | Choice | File | Bool
    bool Required = false,
    string? Default = null,
    string? Placeholder = null,
    IReadOnlyList<string>? Choices = null,      // for Choice
    IReadOnlyList<string>? FileExtensions = null, // for File (picker filter)
    bool SaveFile = false);                     // File: true = save picker, false = open picker
```

A `Password` field is routed to the OS keychain and never written to disk; a
`File` field gets a Browse button wired to the host's save/open picker.

### Custom tool UI (Route B) — `ICustomToolUi`

When the inputs are interdependent (a choice that shows/hides other fields, a
custom layout), a tool can supply its own Avalonia view instead of the generated
form:

```csharp
public interface ICustomToolUi
{
    Control CreateView(IToolUiContext context);   // read/write values by ToolField.Key
}
```

The plugin implements `IToolPlugin` **and** `ICustomToolUi`; the host hosts the
returned control in the tool dialog and still collects values through
`IToolUiContext.GetValue/SetValue`, so `ExecuteAsync` is unchanged. Because the
returned `Control` is an Avalonia type shared across the ALC boundary, add an
Avalonia reference to the plugin `.csproj` with `ExcludeAssets="runtime"` (share
the host's copy) — see [Referencing Avalonia for a Route B view](capabilities.md#referencing-avalonia-for-a-route-b-view).

### A tab instead of a dialog — `IToolDocumentUi` (host API 6)

A tool that implements `IToolDocumentUi` opens as a **tab in the main window**
rather than a dialog, and owns everything inside it:

```csharp
public interface IToolDocumentUi
{
    Control CreateDocument(IToolDocumentContext context);
    Geometry? Icon => null;
}
```

The distinction is lifetime, not looks. A dialog collects input, runs, reports
and closes — the host's generic chrome exists for that shape. A document is
something the user keeps open while working on something else, which is why the
ER diagram (SE-82) is one: it is read alongside the queries it explains, and a
dialog that has to be dismissed to type a query is the wrong container.

Consequences worth knowing before choosing this over Route B:

- `Fields` is never read and **`ExecuteAsync` is never called** — opening the
  tab *is* the action. `ToolTarget` still decides where the menu entry appears.
- `IToolDocumentContext` is narrower than `IToolUiContext`: there are no field
  values, because nothing is being collected. It carries the `IDbProvider` and
  `ConnectionProfile` (so a view can build a schema reader), the launch node,
  the plugin's localizer, and three host actions — `SetTitle`, `OpenQueryEditor`
  and `CloseDocument`.
- Reopening the tool on the same connection, database and node **focuses the
  existing tab** rather than opening a second one.
- If the returned control implements `IDisposable`, the host disposes it when
  the tab closes. A document holds what a dialog never does — a schema snapshot,
  a timer — and without this they live as long as the app.
- **Document tabs are not restored on restart.** The host persists query tabs
  only; a document would have to re-read the schema to come back, and doing that
  silently on every launch is a cost the user did not ask for.

Same Avalonia/ALC rule as Route B: reference Avalonia with
`ExcludeAssets="runtime"` so the control has one type identity with the host.

### Tool manifest

Identical to a provider's, but `type` is `"tool"` and `hostApiVersion` tracks the
**tool** contract (`ToolHostApi.Version`, currently `7`), which versions
separately from the provider contract:

```json
{
  "schemaVersion": 1,
  "id": "universal-backup",
  "type": "tool",
  "name": "Universal Backup & Restore",
  "version": "1.0.0",
  "hostApiVersion": 1,
  "entryAssembly": "DataTray.Tools.UniversalBackup.dll"
}
```

See also: [How discovery and loading work](discovery-and-loading.md).
