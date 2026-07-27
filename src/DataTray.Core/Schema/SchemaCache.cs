using DataTray.Core.Connections;
using DataTray.Core.Providers;
using DataTray.Sdk;
using DataTray.Sdk.Extensibility;

namespace DataTray.Core.Schema;

/// <summary>Per-connection cache of the schema snapshot that drives search and completion.</summary>
public interface ISchemaCache
{
    /// <summary>The cached snapshot for a connection, or null when it has not been built yet.</summary>
    SchemaSnapshot? Get(string connectionId);

    /// <summary>
    /// Walk the provider's lazy tree and cache the snapshot for this connection. Costs N round-trips
    /// (one per container node) but runs once at connect, off the UI thread.
    /// </summary>
    Task BuildAsync(SavedConnection connection, CancellationToken ct = default);

    /// <summary>Forget a connection's snapshot (on disconnect / refresh / delete).</summary>
    void Invalidate(string connectionId);

    /// <summary>Raised after the cache changes (a snapshot was built or invalidated).</summary>
    event Action? Changed;
}

/// <summary>
/// Builds each snapshot by walking the same lazy <see cref="IDbProvider.GetChildNodesAsync"/> the
/// sidebar uses — so it needs no SDK/host-API change and works for every provider shape. It descends
/// only through container nodes and stops at each table/view, reading that relation's Column children.
/// </summary>
public sealed class SchemaCache(IDbProviderRegistry providers, ConnectionService connections)
    : ISchemaCache, ISingletonService
{
    // Safety valve: cap total relations so a pathologically large server can't make the walk run away.
    private const int MaxObjects = 5000;

    private readonly Dictionary<string, SchemaSnapshot> _byConnection = [];
    private readonly Lock _gate = new();

    public event Action? Changed;

    public SchemaSnapshot? Get(string connectionId)
    {
        lock (_gate)
        {
            return _byConnection.TryGetValue(connectionId, out var snapshot) ? snapshot : null;
        }
    }

    public async Task BuildAsync(SavedConnection connection, CancellationToken ct = default)
    {
        var provider = providers.Get(connection.ProviderId);
        var profile = connections.Resolve(connection);

        var objects = new List<SchemaObject>();
        await WalkAsync(provider, profile, [], objects, ct);

        lock (_gate)
        {
            _byConnection[connection.Id] = new SchemaSnapshot(objects);
        }

        Changed?.Invoke();
    }

    public void Invalidate(string connectionId)
    {
        bool removed;
        lock (_gate)
        {
            removed = _byConnection.Remove(connectionId);
        }

        if (removed)
        {
            Changed?.Invoke();
        }
    }

    // Depth-first walk mirroring how the sidebar expands nodes: descend through container kinds,
    // record each table/view together with its columns. The plain profile + ancestors is exactly
    // what the tree passes (providers read the target catalog from the ancestry themselves).
    private async Task WalkAsync(
        IDbProvider provider,
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        List<SchemaObject> sink,
        CancellationToken ct)
    {
        if (sink.Count >= MaxObjects)
        {
            return;
        }

        IReadOnlyList<DbTreeNode> children;
        try
        {
            children = await provider.GetChildNodesAsync(profile, ancestors, ct);
        }
        catch
        {
            // A partial snapshot beats none: skip a branch the provider refuses (permissions, offline).
            return;
        }

        foreach (var child in children)
        {
            ct.ThrowIfCancellationRequested();

            // Never index engine-managed system databases (master/msdb/…), regardless of whether the
            // tree shows them — completion/search over them is just noise.
            if (child.IsSystem)
            {
                continue;
            }

            var path = new List<DbNodeRef>(ancestors) { new(child.Kind, child.Name) };

            if (child.Kind is DbNodeKind.Table or DbNodeKind.View)
            {
                var (columns, foreignKeys) = child.HasChildren
                    ? await RelationDetailsAsync(provider, profile, path, ct)
                    : ([], []);

                sink.Add(new SchemaObject
                {
                    Kind = child.Kind,
                    Database = DbNameOf(ancestors),
                    Schema = SchemaNameOf(ancestors),
                    Name = child.Name,
                    Columns = columns,
                    ForeignKeys = foreignKeys
                });

                if (sink.Count >= MaxObjects)
                {
                    return;
                }
            }
            else if (IsContainer(child.Kind) && child.HasChildren)
            {
                await WalkAsync(provider, profile, path, sink, ct);
            }
        }
    }

    // A relation's columns and outgoing foreign keys, read from one fetch of its child nodes. Providers group
    // columns under a "Columns" folder and FKs under a "Foreign Keys" folder (each a separate round-trip to
    // expand), alongside Indexes/etc.; views expose columns directly and have no FKs. Best-effort throughout —
    // a partial result beats none.
    private static async Task<(IReadOnlyList<SchemaColumn> Columns, IReadOnlyList<SchemaForeignKey> ForeignKeys)>
        RelationDetailsAsync(IDbProvider provider, ConnectionProfile profile, IReadOnlyList<DbNodeRef> tablePath, CancellationToken ct)
    {
        IReadOnlyList<DbTreeNode> children;
        try
        {
            children = await provider.GetChildNodesAsync(profile, tablePath, ct);
        }
        catch
        {
            return ([], []);
        }

        var columns = await ColumnsAsync(provider, profile, tablePath, children, ct);
        var foreignKeys = await ForeignKeysAsync(provider, profile, tablePath, children, ct);
        return (columns, foreignKeys);
    }

    private static async Task<IReadOnlyList<SchemaColumn>> ColumnsAsync(
        IDbProvider provider, ConnectionProfile profile, IReadOnlyList<DbNodeRef> tablePath,
        IReadOnlyList<DbTreeNode> tableChildren, CancellationToken ct)
    {
        var children = tableChildren;
        // A table groups its columns under a "Columns" folder; a view exposes them directly.
        if (tableChildren.FirstOrDefault(n => n.Kind == DbNodeKind.ColumnFolder) is { } folder)
        {
            try
            {
                var folderPath = new List<DbNodeRef>(tablePath) { new(folder.Kind, folder.Name) };
                children = await provider.GetChildNodesAsync(profile, folderPath, ct);
            }
            catch
            {
                return [];
            }
        }

        return children
            .Where(node => node.Kind == DbNodeKind.Column)
            .Select(node => new SchemaColumn(node.Name, node.Detail))
            .ToList();
    }

    private static async Task<IReadOnlyList<SchemaForeignKey>> ForeignKeysAsync(
        IDbProvider provider, ConnectionProfile profile, IReadOnlyList<DbNodeRef> tablePath,
        IReadOnlyList<DbTreeNode> tableChildren, CancellationToken ct)
    {
        if (tableChildren.FirstOrDefault(n => n.Kind == DbNodeKind.ForeignKeyFolder) is not { } folder)
        {
            return [];
        }

        IReadOnlyList<DbTreeNode> fkNodes;
        try
        {
            var folderPath = new List<DbNodeRef>(tablePath) { new(folder.Kind, folder.Name) };
            fkNodes = await provider.GetChildNodesAsync(profile, folderPath, ct);
        }
        catch
        {
            return [];
        }

        return fkNodes
            .Where(node => node.Kind == DbNodeKind.ForeignKey)
            .Select(node => ParseForeignKey(node.Detail))
            .Where(fk => fk is not null)
            .Select(fk => fk!)
            .ToList();
    }

    // Providers describe an FK node uniformly as "column → refTable.refColumn" (see each provider's FK loader).
    // Parse that shared shape back into structured data for JOIN-condition hints; null when it doesn't match
    // (a composite/rendered form we don't recognise), so an odd entry is skipped rather than mis-suggested.
    public static SchemaForeignKey? ParseForeignKey(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        var arrow = detail.IndexOf('→');
        if (arrow < 0)
        {
            return null;
        }

        var column = detail[..arrow].Trim();
        var target = detail[(arrow + 1)..].Trim();
        var dot = target.LastIndexOf('.');
        if (column.Length == 0 || dot <= 0 || dot >= target.Length - 1)
        {
            return null;
        }

        return new SchemaForeignKey(column, target[..dot].Trim(), target[(dot + 1)..].Trim());
    }

    private static string? DbNameOf(IReadOnlyList<DbNodeRef> path) =>
        path.FirstOrDefault(r => r.Kind == DbNodeKind.Database)?.Name;

    private static string? SchemaNameOf(IReadOnlyList<DbNodeRef> path) =>
        path.FirstOrDefault(r => r.Kind == DbNodeKind.Schema)?.Name;

    private static bool IsContainer(DbNodeKind kind) => kind is
        DbNodeKind.Database or DbNodeKind.SchemaFolder or DbNodeKind.Schema or
        DbNodeKind.TableFolder or DbNodeKind.ViewFolder or DbNodeKind.Group or DbNodeKind.DatabaseFolder;
}
