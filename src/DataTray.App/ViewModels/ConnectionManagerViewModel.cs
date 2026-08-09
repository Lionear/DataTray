using System.Collections.ObjectModel;
using DataTray.Core.Connections;
using DataTray.Core.Connections.Import;
using DataTray.Core.Localization;
using DataTray.Core.Providers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DataTray.App.ViewModels;

/// <summary>Which detail panel the right side of the Connection Manager shows.</summary>
public enum ManagerPane
{
    Empty,
    Connection,
    Folder
}

/// <summary>Where a drag lands relative to the hovered node: inside a folder (reparent, default) or
/// as a sibling immediately before/after it (reorder within the same parent).</summary>
public enum DropPosition
{
    Inside,
    Before,
    After
}

/// <summary>
/// Backs the Connection Manager window (master-detail). The left tree groups saved connections into
/// nested folders derived from each connection's <c>/</c>-joined <see cref="SavedConnection.Folder"/>
/// path; the right side edits the selected connection (reusing <see cref="ConnectionDialogViewModel"/>),
/// renames/deletes the selected folder, or shows an empty state. Folders exist only as path prefixes,
/// so a freshly created, still-empty folder is kept in-memory (<see cref="_emptyFolders"/>) until a
/// connection lands in it.
/// </summary>
public partial class ConnectionManagerViewModel : ViewModelBase
{
    private readonly ConnectionService _connections;
    private readonly Func<ConnectionDialogViewModel> _detailFactory;
    private readonly IDbProviderRegistry _providers;

    // Folder paths that have no connection under them yet (New Folder / emptied by a move). Without this
    // they'd vanish on the next rebuild, since a folder is otherwise just a prefix on a connection.
    private readonly HashSet<string> _emptyFolders = new(StringComparer.OrdinalIgnoreCase);

    private ConnectionManagerNode? _selectedFolder;

    // Cached at each RebuildTree so folder rows can be inserted alphabetically among peers that share the
    // fallback rank, but before those with a manually assigned lower index. Full-path → index; smaller wins.
    private IReadOnlyDictionary<string, int> _folderOrder = new Dictionary<string, int>();

    public ConnectionManagerViewModel(
        ConnectionService connections,
        Func<ConnectionDialogViewModel> detailFactory,
        ILocalizer localizer,
        IDbProviderRegistry providers)
    {
        _connections = connections;
        _detailFactory = detailFactory;
        _providers = providers;
        Loc = localizer;
        RebuildTree();
    }

    public ILocalizer Loc { get; }

    /// <summary>Set by the window so the VM can ask a yes/no question (title, message); false if unavailable.</summary>
    public Func<string, string, Task<bool>>? ConfirmRequested { get; set; }

    /// <summary>Set by the window: show the discovered DataGrip/DBeaver connections and return the ones
    /// the user ticked (empty when cancelled). Null = no UI available, so the import command is a no-op.</summary>
    public Func<IReadOnlyList<DiscoveredConnection>, Task<IReadOnlyList<DiscoveredConnection>>>? ImportExternalRequested { get; set; }

    /// <summary>The manager tree: nested folder nodes + connection leaves.</summary>
    public ObservableCollection<ConnectionManagerNode> Nodes { get; } = [];

    [ObservableProperty]
    private ConnectionManagerNode? _selectedNode;

    [ObservableProperty]
    private string _filter = string.Empty;

    // The connection form shown when a connection (or a new draft) is selected; reused from the old modal.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanActOnConnection))]
    private ConnectionDialogViewModel? _detail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnectionPane))]
    [NotifyPropertyChangedFor(nameof(IsFolderPane))]
    [NotifyPropertyChangedFor(nameof(IsEmptyPane))]
    private ManagerPane _pane;

    /// <summary>Editable folder name in the folder-detail panel.</summary>
    [ObservableProperty]
    private string _folderName = string.Empty;

    public bool IsConnectionPane => Pane == ManagerPane.Connection;
    public bool IsFolderPane => Pane == ManagerPane.Folder;
    public bool IsEmptyPane => Pane == ManagerPane.Empty;

    /// <summary>Duplicate/Delete apply only to a saved (persisted) connection in the detail panel.</summary>
    public bool CanActOnConnection => Detail is { IsEditing: true };

    /// <summary>Existing folder paths (including every intermediate parent), for the detail form's
    /// folder type-ahead. Distinct + sorted; a connection may still type a brand-new path.</summary>
    public IReadOnlyList<string> FolderSuggestions =>
        _connections.List()
            .Select(c => c.Folder)
            .Concat(_emptyFolders)
            .SelectMany(AncestorsAndSelf)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Footer summary: "{connections} · {folders}".</summary>
    public string Summary
    {
        get
        {
            var connections = _connections.List().Count;
            var folders = AllFolders(Nodes).Count();
            return Loc.Get("ManagerSummary", connections, folders);
        }
    }

    // --- Pre-positioning called by MainViewModel before the window opens ---

    /// <summary>Open with an existing connection selected (right-click Edit… from the sidebar).</summary>
    public void SelectConnection(string id)
    {
        if (FindConnection(Nodes, id) is { } node)
        {
            SelectedNode = node;
        }
    }

    /// <summary>Open on a fresh connection draft, optionally pre-filed into <paramref name="folder"/>.</summary>
    public void StartNewConnection(string? folder)
    {
        var detail = _detailFactory();
        if (!string.IsNullOrWhiteSpace(folder))
        {
            detail.Folder = folder;
        }

        Detail = detail;
        SelectedNode = null;
        _selectedFolder = null;
        Pane = ManagerPane.Connection;
    }

    // --- Selection ---

    partial void OnSelectedNodeChanged(ConnectionManagerNode? value)
    {
        // Null is only ever set programmatically (e.g. while starting a draft) — leave the pane alone.
        if (value is null)
        {
            return;
        }

        if (value.IsFolder)
        {
            _selectedFolder = value;
            FolderName = value.Name;
            Detail = null;
            Pane = ManagerPane.Folder;
        }
        else
        {
            _selectedFolder = null;
            var detail = _detailFactory();
            detail.LoadForEdit(value.Connection!);
            Detail = detail;
            Pane = ManagerPane.Connection;
        }
    }

    partial void OnFilterChanged(string value) => RebuildTree(SelectedNode?.Connection?.Id);

    // --- Connection actions ---

    [RelayCommand]
    private void SaveConnection()
    {
        if (Detail is null)
        {
            return;
        }

        var saved = Detail.Save();
        _emptyFolders.Remove(saved.Folder ?? string.Empty);
        RebuildTree(saved.Id);
        OnPropertyChanged(nameof(Summary));
        ConnectionsChanged?.Invoke();
    }

    [RelayCommand]
    private void NewConnection() => StartNewConnection(SelectedFolderContext());

    /// <summary>
    /// Import connections found in DataGrip/DBeaver (SE-233). Scanning happens off the UI thread; the
    /// window shows the picker and returns what the user ticked. Imported connections never carry a
    /// password — the other clients keep those in their own credential stores — so each one lands with
    /// its fields filled and the password prompt left to first connect.
    /// </summary>
    [RelayCommand]
    private async Task ImportExternal()
    {
        if (ImportExternalRequested is null)
        {
            return;
        }

        var found = await Task.Run(() => ExternalConnectionImport.Discover(FieldKeysOf));
        var chosen = await ImportExternalRequested(found);
        if (chosen.Count == 0)
        {
            return;
        }

        // Shared with the first-run wizard (SE-239) so unique-naming and the save shape can't drift apart.
        var saved = ImportedConnections.SaveAll(_connections, chosen);

        RebuildTree(saved.LastOrDefault());
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(FolderSuggestions));
        ConnectionsChanged?.Invoke();
    }

    // The provider's declared field keys, or null when its plugin isn't installed — the importer uses the
    // same answer for "can we import this engine?" and "which key does it want the host under?".
    private IReadOnlyList<string>? FieldKeysOf(string providerId) =>
        _providers.TryGet(providerId, out var provider)
            ? provider.ConnectionFields.Select(f => f.Key).ToList()
            : null;

    [RelayCommand]
    private void Discard()
    {
        Detail = null;
        SelectedNode = null;
        Pane = ManagerPane.Empty;
    }

    [RelayCommand]
    private async Task DuplicateConnection()
    {
        if (Detail is not { IsEditing: true } || SelectedNode?.Connection is not { } connection)
        {
            return;
        }

        var copy = _connections.Duplicate(connection.Id, $"{connection.Name} {Loc["CopySuffix"]}");
        RebuildTree(copy.Id);
        OnPropertyChanged(nameof(Summary));
        ConnectionsChanged?.Invoke();
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task DeleteConnection()
    {
        if (Detail is not { IsEditing: true } || SelectedNode?.Connection is not { } connection)
        {
            return;
        }

        if (ConfirmRequested is not null
            && !await ConfirmRequested(Loc["Delete"], Loc.Get("DeleteConnectionConfirm", connection.Name)))
        {
            return;
        }

        _connections.Delete(connection.Id);
        Detail = null;
        SelectedNode = null;
        Pane = ManagerPane.Empty;
        RebuildTree();
        OnPropertyChanged(nameof(Summary));
        ConnectionsChanged?.Invoke();
    }

    // --- Folder actions ---

    [RelayCommand]
    private void NewFolder()
    {
        var parent = SelectedFolderContext();
        var path = UniqueFolderPath(parent, Loc["NewFolderName"]);
        _emptyFolders.Add(path);
        RebuildTree(selectFolderPath: path);
        OnPropertyChanged(nameof(Summary));
    }

    [RelayCommand]
    private void RenameFolder()
    {
        if (_selectedFolder is not { FolderPath: { } oldPath })
        {
            return;
        }

        var newName = FolderName.Trim();
        if (newName.Length == 0 || newName.Contains('/') || string.Equals(newName, _selectedFolder.Name, StringComparison.Ordinal))
        {
            return;
        }

        var parent = ParentPath(oldPath);
        var newPath = parent is null ? newName : $"{parent}/{newName}";
        MoveFolderPath(oldPath, newPath);
        RebuildTree(selectFolderPath: newPath);
        ConnectionsChanged?.Invoke();
    }

    [RelayCommand]
    private async Task DeleteFolder()
    {
        if (_selectedFolder is not { FolderPath: { } path })
        {
            return;
        }

        var affected = _connections.List().Where(c => IsUnderOrEqual(c.Folder, path)).ToList();
        if (affected.Count > 0)
        {
            if (ConfirmRequested is null
                || !await ConfirmRequested(Loc["DeleteFolder"], Loc.Get("DeleteFolderConfirm", affected.Count)))
            {
                return;
            }

            foreach (var connection in affected)
            {
                _connections.SetFolder(connection, null);
            }
        }

        _emptyFolders.RemoveWhere(p => IsUnderOrEqual(p, path));
        _selectedFolder = null;
        SelectedNode = null;
        Pane = ManagerPane.Empty;
        RebuildTree();
        OnPropertyChanged(nameof(Summary));
        if (affected.Count > 0)
        {
            ConnectionsChanged?.Invoke();
        }
    }

    // --- Drag & drop (invoked by the window) ---

    /// <summary>True when <paramref name="dragged"/> may be dropped at <paramref name="position"/> relative
    /// to <paramref name="target"/> (a root drop = target null + Inside): never onto itself, its own
    /// descendant, or a spot that would be a no-op.</summary>
    public bool CanDrop(ConnectionManagerNode dragged, ConnectionManagerNode? target, DropPosition position)
    {
        var parent = ResolveParent(target, position);
        if (parent is not null && !parent.IsFolder)
        {
            return false; // parent must be a folder (or null = root)
        }

        var parentPath = parent?.FolderPath;

        // Reordering next to the dragged node itself in its current parent = no-op.
        if (position != DropPosition.Inside && ReferenceEquals(dragged, target))
        {
            return false;
        }

        if (dragged.IsConnection)
        {
            var current = dragged.Connection!.Folder;
            if (position == DropPosition.Inside)
            {
                // Inside is only a valid drop for connections when it actually reparents them.
                return !string.Equals(current, parentPath, StringComparison.Ordinal);
            }

            // Before/After on a sibling: parent match is fine (pure reorder) or reparent (change folder).
            return true;
        }

        if (dragged.FolderPath is not { } sourcePath)
        {
            return false;
        }

        // Folder can't drop onto itself or one of its descendants (would create a cycle).
        if (parentPath is not null
            && (string.Equals(parentPath, sourcePath, StringComparison.OrdinalIgnoreCase)
                || IsUnderOrEqual(parentPath, sourcePath)))
        {
            return false;
        }

        if (position == DropPosition.Inside)
        {
            // Inside its current parent = no-op.
            return !string.Equals(ParentPath(sourcePath), parentPath, StringComparison.Ordinal);
        }

        return true;
    }

    /// <summary>The folder node for a given path (null path/empty = the root, returns null).</summary>
    public ConnectionManagerNode? FindFolderNode(string? path) =>
        string.IsNullOrEmpty(path) ? null : FindFolder(Nodes, path);

    /// <summary>Reparent and/or reorder a dragged node relative to <paramref name="target"/>.</summary>
    public void Drop(ConnectionManagerNode dragged, ConnectionManagerNode? target, DropPosition position)
    {
        if (!CanDrop(dragged, target, position))
        {
            return;
        }

        var parent = ResolveParent(target, position);
        var parentPath = parent?.FolderPath;

        ApplyDrop(dragged, target, position, parentPath);
        OnPropertyChanged(nameof(Summary));
        ConnectionsChanged?.Invoke();
    }

    /// <summary>Raised when the tree contents change (Save, drop-reorder, delete, folder rename). The
    /// sidebar hooks this to <see cref="MainViewModel.SyncConnectionsFromStore"/> so its schema tree
    /// updates without waiting for the dialog to close — and without collapsing open subtrees.</summary>
    public event Action? ConnectionsChanged;

    // The parent scope for a drop: Inside → the target itself (or root); Before/After → the target's parent.
    private ConnectionManagerNode? ResolveParent(ConnectionManagerNode? target, DropPosition position)
    {
        if (target is null)
        {
            return null;
        }

        if (position == DropPosition.Inside)
        {
            return target.IsFolder ? target : FindFolderNode(target.Connection!.Folder);
        }

        // Sibling drop: parent of the target row.
        return target.IsFolder
            ? FindFolderNode(ParentPath(target.FolderPath!))
            : FindFolderNode(target.Connection!.Folder);
    }

    // A single row's identity for the mixed-scope reorder: either a saved connection or a folder path.
    // Kept as a discriminated pair so scope-siblings can be interleaved and restamped in one pass.
    private readonly record struct ScopeItem(SavedConnection? Connection, string? FolderPath)
    {
        public bool IsConnection => Connection is not null;
    }

    // Mixed-scope reorder: folders and connections share the numeric order in `parentPath`, so a drag can
    // land a connection between two folders (or the other way around). Restamps the affected scope 1..N
    // for both SortOrder (connections) and folderOrder[path], then writes both back in one file operation.
    private void ApplyDrop(ConnectionManagerNode dragged, ConnectionManagerNode? target, DropPosition position, string? parentPath)
    {
        var (moved, oldFolderPath, newFolderPath) = PrepareMovedItem(dragged, parentPath);

        // If a folder is being renamed/reparented, rewrite its descendants' Folder before we snapshot the
        // scope siblings — otherwise nested-connection paths would be stale on write.
        if (oldFolderPath is not null && newFolderPath is not null
            && !string.Equals(oldFolderPath, newFolderPath, StringComparison.Ordinal))
        {
            MoveFolderPath(oldFolderPath, newFolderPath);
        }

        // Scope siblings straight out of the current tree, in their current visual order, minus the
        // dragged row (which we'll insert fresh at the requested spot).
        var siblings = ScopeSiblings(parentPath, exclude: dragged, renamedFolder: (oldFolderPath, newFolderPath));

        var insertAt = ResolveInsertIndex(siblings, target, position, oldFolderPath, newFolderPath);
        siblings.Insert(insertAt, moved);

        RestampAndPersist(siblings, parentPath, dragged, moved);

        if (moved.IsConnection)
        {
            RebuildTree(moved.Connection!.Id);
        }
        else
        {
            RebuildTree(selectFolderPath: moved.FolderPath);
        }
    }

    // Build the "moved" scope-item and the old/new folder paths for a folder drag (both null for a
    // connection drag). The moved connection carries its new Folder inline so the restamp sees the right
    // scope; the moved folder carries its new full path.
    private static (ScopeItem Moved, string? OldFolderPath, string? NewFolderPath) PrepareMovedItem(
        ConnectionManagerNode dragged, string? parentPath)
    {
        if (dragged.IsConnection)
        {
            var updated = dragged.Connection! with { Folder = parentPath };
            return (new ScopeItem(updated, null), null, null);
        }

        var sourcePath = dragged.FolderPath!;
        var newPath = parentPath is null ? dragged.Name : $"{parentPath}/{dragged.Name}";
        return (new ScopeItem(null, newPath), sourcePath, newPath);
    }

    // The scope-siblings for a given parent, in current visual order. When a folder drag renamed itself,
    // the "old" path is filtered out (its new path is inserted separately by the caller).
    private List<ScopeItem> ScopeSiblings(string? parentPath, ConnectionManagerNode exclude, (string? Old, string? New) renamedFolder)
    {
        var parentChildren = parentPath is null ? Nodes : (FindFolderNode(parentPath)?.Children ?? Nodes);
        var items = new List<ScopeItem>();
        foreach (var node in parentChildren)
        {
            if (ReferenceEquals(node, exclude))
            {
                continue;
            }

            if (node.IsFolder)
            {
                var path = node.FolderPath!;
                if (renamedFolder.Old is not null && string.Equals(path, renamedFolder.Old, StringComparison.Ordinal))
                {
                    continue;
                }

                items.Add(new ScopeItem(null, path));
            }
            else if (node.Connection is { } c)
            {
                items.Add(new ScopeItem(c, null));
            }
        }

        return items;
    }

    private static int ResolveInsertIndex(
        List<ScopeItem> siblings, ConnectionManagerNode? target, DropPosition position,
        string? oldFolderPath, string? newFolderPath)
    {
        if (target is null || position == DropPosition.Inside)
        {
            return siblings.Count;
        }

        var targetIndex = IndexOfTarget(siblings, target, oldFolderPath, newFolderPath);
        if (targetIndex < 0)
        {
            return siblings.Count;
        }

        return position == DropPosition.Before ? targetIndex : targetIndex + 1;
    }

    private static int IndexOfTarget(List<ScopeItem> siblings, ConnectionManagerNode target, string? oldFolderPath, string? newFolderPath)
    {
        for (var i = 0; i < siblings.Count; i++)
        {
            var item = siblings[i];
            if (target.IsFolder && item.FolderPath is { } fp)
            {
                var effective = oldFolderPath is not null && string.Equals(target.FolderPath, oldFolderPath, StringComparison.Ordinal)
                    ? newFolderPath
                    : target.FolderPath;
                if (string.Equals(fp, effective, StringComparison.Ordinal))
                {
                    return i;
                }
            }
            else if (target.IsConnection && item.Connection is { } c
                && string.Equals(c.Id, target.Connection!.Id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    // Stamp 1..N onto the mixed scope, merge the untouched connections in, and write both maps in a
    // single store call so the file stays consistent even under a crash mid-write.
    private void RestampAndPersist(List<ScopeItem> siblings, string? parentPath, ConnectionManagerNode dragged, ScopeItem moved)
    {
        var folderOrder = new Dictionary<string, int>(_connections.ListFolderOrder(), StringComparer.Ordinal);

        // Wipe existing indices for every folder currently in this scope — they'll get restamped below,
        // or drop out of the map entirely (falling back to alphabetical) if they left the scope.
        foreach (var path in _folderOrder.Keys
            .Where(p => string.Equals(ParentPath(p), parentPath, StringComparison.Ordinal))
            .ToList())
        {
            folderOrder.Remove(path);
        }

        // Restamp connections in the scope; other connections keep their SortOrder as-is.
        var draggedId = dragged.IsConnection ? dragged.Connection!.Id : null;
        var scopeConnectionIds = siblings
            .Where(s => s.IsConnection)
            .Select(s => s.Connection!.Id)
            .ToHashSet();

        var final = new List<SavedConnection>();
        foreach (var connection in _connections.List())
        {
            if (draggedId is not null && string.Equals(connection.Id, draggedId, StringComparison.Ordinal))
            {
                continue; // will be re-added from the restamped scope
            }

            if (scopeConnectionIds.Contains(connection.Id))
            {
                continue; // will be re-added from the restamped scope
            }

            final.Add(connection);
        }

        for (var i = 0; i < siblings.Count; i++)
        {
            var item = siblings[i];
            var index = i + 1;
            if (item.Connection is { } c)
            {
                final.Add(c with { SortOrder = index });
            }
            else if (item.FolderPath is { } fp)
            {
                folderOrder[fp] = index;
            }
        }

        _connections.ApplyReorder(final, folderOrder);
    }

    // --- Tree building ---

    private void RebuildTree(string? selectConnectionId = null, string? selectFolderPath = null)
    {
        Nodes.Clear();
        _folderOrder = _connections.ListFolderOrder();

        var filter = Filter.Trim();
        var connections = _connections.List()
            .Where(c => filter.Length == 0
                || c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || (c.Folder?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderBy(c => c.Folder ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.SortOrder)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var connection in connections)
        {
            ResolveFolderChildren(connection.Folder).Add(
                ConnectionManagerNode.ForConnection(connection, ResolveIcon(connection.ProviderId)));
        }

        // Keep still-empty folders visible (hidden while filtering, since they can't match a query).
        if (filter.Length == 0)
        {
            foreach (var path in _emptyFolders)
            {
                EnsureFolderPath(path);
            }
        }

        RestoreSelection(selectConnectionId, selectFolderPath);
        OnPropertyChanged(nameof(FolderSuggestions));
    }

    private void RestoreSelection(string? connectionId, string? folderPath)
    {
        if (connectionId is not null && FindConnection(Nodes, connectionId) is { } connectionNode)
        {
            SelectedNode = connectionNode;
        }
        else if (folderPath is not null && FindFolder(Nodes, folderPath) is { } folderNode)
        {
            SelectedNode = folderNode;
        }
        else if (SelectedNode is null && Detail is null)
        {
            // Nothing selected and no draft in progress → empty state. A draft (Detail set, no tree row)
            // is left alone so filtering the list doesn't discard a half-filled new connection.
            Pane = ManagerPane.Empty;
        }
    }

    // TryGet keeps a saved connection to an uninstalled provider from throwing here (Store-only providers,
    // see Codebase.md gotcha #7 / startup-crash fix); missing provider → null image → generic vector fallback.
    private Avalonia.Media.IImage? ResolveIcon(string providerId) =>
        _providers.TryGet(providerId, out var provider) ? PluginIconRenderer.Render(provider.Icon) : null;

    // Walk the /-path, creating folder nodes as needed; return the collection the connection lives in.
    private ObservableCollection<ConnectionManagerNode> ResolveFolderChildren(string? folderPath)
    {
        var current = Nodes;
        var soFar = string.Empty;
        foreach (var segment in SplitPath(folderPath))
        {
            soFar = soFar.Length == 0 ? segment : $"{soFar}/{segment}";
            var folder = current.FirstOrDefault(n => n.IsFolder && n.Name == segment)
                ?? AddSorted(current, ConnectionManagerNode.ForFolder(segment, soFar));
            current = folder.Children;
        }

        return current;
    }

    private void EnsureFolderPath(string fullPath)
    {
        var current = Nodes;
        var soFar = string.Empty;
        foreach (var segment in SplitPath(fullPath))
        {
            soFar = soFar.Length == 0 ? segment : $"{soFar}/{segment}";
            var folder = current.FirstOrDefault(n => n.IsFolder && n.Name == segment)
                ?? AddSorted(current, ConnectionManagerNode.ForFolder(segment, soFar));
            current = folder.Children;
        }
    }

    // Folders sort before connections; within each group the manual sort index (folder-order map for
    // folders, SortOrder for connections) wins, with the display name as a stable tiebreaker for the
    // unsorted-legacy case (both indices 0 → alphabetical).
    private ConnectionManagerNode AddSorted(ObservableCollection<ConnectionManagerNode> container, ConnectionManagerNode node)
    {
        var index = 0;
        while (index < container.Count && Ranks(container[index], node) <= 0)
        {
            index++;
        }

        container.Insert(index, node);
        return node;
    }

    private int Ranks(ConnectionManagerNode existing, ConnectionManagerNode incoming)
    {
        // Folders and connections share the same numeric slot per scope, so a manual drag can place a
        // connection between two folders (or vice versa). Fallback for the unsorted-legacy case (both
        // indices int.MaxValue / 0 → alphabetical), with folders winning the tie so a fresh install
        // still shows folders above connections until something is dragged.
        var manualDelta = ManualIndex(existing) - ManualIndex(incoming);
        if (manualDelta != 0)
        {
            return manualDelta;
        }

        if (existing.IsFolder != incoming.IsFolder)
        {
            return existing.IsFolder ? -1 : 1;
        }

        return string.Compare(existing.Name, incoming.Name, StringComparison.OrdinalIgnoreCase);
    }

    // Manual sort indices are 1-based (see ApplyDrop). 0 / missing means "unsorted" and pushes the node
    // into the alphabetical tail — so a scope with no drags stays purely alphabetical, and a partial drag
    // still puts the reordered items at the top of their scope in the exact requested order.
    private int ManualIndex(ConnectionManagerNode node)
    {
        if (node.IsFolder)
        {
            return node.FolderPath is { } path
                && _folderOrder.TryGetValue(path, out var idx) && idx > 0
                    ? idx
                    : int.MaxValue;
        }

        var sortOrder = node.Connection?.SortOrder ?? 0;
        return sortOrder > 0 ? sortOrder : int.MaxValue;
    }

    // --- Folder path helpers ---

    // Rewrite every connection (and empty-folder entry) under oldPath so it hangs off newPath instead —
    // and re-key the folder-order map so a rename/reparent keeps its manual sort position (otherwise the
    // map would still point at the old path and the folder would fall back to alphabetical).
    private void MoveFolderPath(string oldPath, string newPath)
    {
        foreach (var connection in _connections.List().Where(c => IsUnderOrEqual(c.Folder, oldPath)).ToList())
        {
            _connections.SetFolder(connection, newPath + connection.Folder![oldPath.Length..]);
        }

        var moved = _emptyFolders.Where(p => IsUnderOrEqual(p, oldPath)).ToList();
        foreach (var path in moved)
        {
            _emptyFolders.Remove(path);
            _emptyFolders.Add(newPath + path[oldPath.Length..]);
        }

        var existingOrder = _connections.ListFolderOrder();
        var remapped = new Dictionary<string, int>(existingOrder, StringComparer.Ordinal);
        var changed = false;
        foreach (var (path, idx) in existingOrder)
        {
            if (!IsUnderOrEqual(path, oldPath))
            {
                continue;
            }

            remapped.Remove(path);
            remapped[newPath + path[oldPath.Length..]] = idx;
            changed = true;
        }

        if (changed)
        {
            _connections.ApplyReorder(_connections.List(), remapped);
        }
    }

    // The folder path context for a New Connection/New Folder: the selected folder, or the folder the
    // selected connection lives in (null = root).
    private string? SelectedFolderContext() => SelectedNode switch
    {
        { IsFolder: true, FolderPath: var path } => path,
        { IsConnection: true, Connection.Folder: var folder } => folder,
        _ => null
    };

    private string UniqueFolderPath(string? parent, string baseName)
    {
        string Candidate(string name) => parent is null ? name : $"{parent}/{name}";

        var path = Candidate(baseName);
        var counter = 2;
        while (FolderPathExists(path))
        {
            path = Candidate($"{baseName} {counter++}");
        }

        return path;
    }

    private bool FolderPathExists(string path) =>
        _emptyFolders.Contains(path) || _connections.List().Any(c => IsUnderOrEqual(c.Folder, path));

    // "Klanten/Klant A" -> "Klanten", "Klanten/Klant A" so parent folders are suggestible on their own.
    private static IEnumerable<string> AncestorsAndSelf(string? path)
    {
        var soFar = string.Empty;
        foreach (var segment in SplitPath(path))
        {
            soFar = soFar.Length == 0 ? segment : $"{soFar}/{segment}";
            yield return soFar;
        }
    }

    private static string[] SplitPath(string? folderPath) =>
        string.IsNullOrWhiteSpace(folderPath)
            ? []
            : folderPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? ParentPath(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? null : path[..index];
    }

    // True when `folder` equals `path` or sits beneath it (so "Klanten/Klant A" is under "Klanten").
    private static bool IsUnderOrEqual(string? folder, string path) =>
        folder is not null
        && (string.Equals(folder, path, StringComparison.Ordinal) || folder.StartsWith(path + "/", StringComparison.Ordinal));

    private static ConnectionManagerNode? FindConnection(IEnumerable<ConnectionManagerNode> nodes, string id)
    {
        foreach (var node in nodes)
        {
            if (node.IsConnection && node.Connection!.Id == id)
            {
                return node;
            }

            if (node.IsFolder && FindConnection(node.Children, id) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static ConnectionManagerNode? FindFolder(IEnumerable<ConnectionManagerNode> nodes, string path)
    {
        foreach (var node in nodes.Where(n => n.IsFolder))
        {
            if (string.Equals(node.FolderPath, path, StringComparison.Ordinal))
            {
                return node;
            }

            if (FindFolder(node.Children, path) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static IEnumerable<ConnectionManagerNode> AllFolders(IEnumerable<ConnectionManagerNode> nodes)
    {
        foreach (var node in nodes.Where(n => n.IsFolder))
        {
            yield return node;
            foreach (var nested in AllFolders(node.Children))
            {
                yield return nested;
            }
        }
    }
}
