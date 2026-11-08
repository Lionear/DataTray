← [Plugins overview](../PLUGINS.md)

## Node menus and table drag (host API 9)

### Node menu items (`INodeMenuPlugin`)

A plugin with the `menu` capability can add items to the context menu of **table and view** nodes in the tree.
Implement `INodeMenuPlugin` and return its items from `NodeMenuItems`; each is a `NodeMenuContribution`:

| Member | Purpose |
|---|---|
| `Id`, `Title` | Stable id and menu text. |
| `AppliesTo(NodeMenuTarget)` | Per-node filter, evaluated each time the menu opens. |
| `InvokeAsync(NodeMenuTarget, IHostUi)` | Runs when the item is clicked. |

`NodeMenuTarget` carries `Connection`, `Database`, `Schema`, `Name`, `IsView` and `ActiveDocument`. The active
document is the selected tab only when it is a **query** tab on the same connection as the node; otherwise it is
null. A failure in `AppliesTo` (the item is left out) or in `InvokeAsync` is reported to Output and never
reaches the tree or other plugins.

### Dragging a table or view (`DraggedNode`)

Dragging a table or view out of the tree carries two things:

- **Plain text**: the dialect-qualified name. This is what an editor inserts when the node is dropped on it.
- **`DraggedNode`**: JSON of `DraggedNode(ConnectionId, Database, Schema, Name, IsView)`, for panes that accept
  drops.

The JSON is stored under `DraggedNode.Format` (`"application/x-datatray-node"`) as an **in-process** format, because
Avalonia 12 rejects `/` in application format names. Read it with the same call:

```csharp
var format = DataFormat.CreateInProcessFormat<string>(DraggedNode.Format);
var json = e.DataTransfer.TryGetValue(format);
var node = json is null ? null : JsonSerializer.Deserialize<DraggedNode>(json);
```

While the editor is linked to a pane it is read-only, so it refuses the drop.
