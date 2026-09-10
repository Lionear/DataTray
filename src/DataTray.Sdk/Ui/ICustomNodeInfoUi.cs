using Avalonia.Controls;
using DataTray.Sdk.Connections;
using DataTray.Sdk.Schema;

namespace DataTray.Sdk.Ui;

/// <summary>
/// Optional capability an <c>IDbProvider</c> may also implement to supply a read-only "properties"/"info"
/// view for a schema-tree node — e.g. SQL Server's Database Properties dialog. A third Route-B capability
/// alongside <see cref="ICustomConnectionUi"/> (advanced connection settings) and <c>ICustomToolUi</c>
/// (tool dialog): the provider owns an Avalonia <see cref="Control"/> that queries its own live data, and
/// the host shows it in generic dialog chrome (title + content + Close). Unlike a tool there is no
/// Execute/progress/log — it is purely informational, so it does not go through the tool registry.
/// </summary>
/// <remarks>
/// This assembly and Avalonia are shared across the plugin ALC boundary (<c>ProviderLoadContext</c>) so
/// the returned control has a single type identity with the host. Providers that don't implement this
/// simply offer no "Properties…" item. Additive optional-interface check — no host API bump needed, same
/// precedent as <see cref="ICustomConnectionUi"/>.
/// </remarks>
public interface ICustomNodeInfoUi
{
    /// <summary>True when this provider offers an info view for the given node (e.g. only Database nodes).</summary>
    bool HasInfoFor(DbNodeRef node);

    /// <summary>Dialog title for the node's info view (e.g. "Database Properties").</summary>
    string InfoTitle(DbNodeRef node);

    /// <summary>Build the read-only info view. The view queries its own live data via <paramref name="context"/>.</summary>
    Control CreateInfoView(NodeInfoContext context);

    /// <summary>
    /// True when the view for this node brings its own footer and the host should leave off its Close row —
    /// a properties dialog that writes needs OK/Cancel, and a Close button underneath them reads as a third,
    /// different action. The host also refreshes the node's parent after such a dialog, since it may have
    /// changed what the tree shows. False by default: the original contract is a read-only view.
    /// </summary>
    bool InfoViewOwnsActionBar(DbNodeRef node) => false;
}

/// <summary>
/// Everything a provider-owned node view needs to query its own live data: the connection profile (already
/// resolved to the target database by the host), the node it was opened on, and the provider itself. Also
/// carried by <see cref="ICustomCreateUi"/>, whose views are opened on a node the same way and need the
/// same four things — a second near-identical record would drift from this one.
/// </summary>
/// <remarks>
/// The positional members are the original read-only trio. The rest are additive with defaults, so a
/// provider built against an older SDK compiles and runs unchanged, and a view that reads them on an older
/// host gets the empty/no-op value rather than a missing member.
/// </remarks>
public sealed record NodeInfoContext(ConnectionProfile Profile, DbNodeRef Node, IDbProvider Provider)
{
    /// <summary>The nodes from the connection root down to <see cref="Node"/>, inclusive. <see cref="Node"/>
    /// alone does not identify an object: an index is named within its table and an "Indexes" folder is
    /// called "Indexes" under every table, so a view opened on either cannot query what it is describing.
    /// The same ancestry <c>ToolExecutionContext.NodePath</c> and <c>SecurityUiContext.Ancestors</c> carry.
    /// Empty default — a view that reads it should say what is missing rather than guess.</summary>
    public IReadOnlyList<DbNodeRef> NodePath { get; init; } = [];

    /// <summary>The nearest ancestor of <paramref name="kind"/> on the way down to this node (the node
    /// itself included), or null when there is none. Same question and same answer as
    /// <c>ToolExecutionContext.Ancestor</c>.</summary>
    public string? Ancestor(DbNodeKind kind) => NodePath.LastOrDefault(n => n.Kind == kind)?.Name;

    /// <summary>Open a new query tab holding <paramref name="sql"/>, against the same connection and
    /// database this view was opened on — what a properties dialog's "Script" button does, and the
    /// counterpart of <c>IToolHost.OpenQueryEditor</c> for tools. Null on a host that does not offer it,
    /// so a view must check before showing the button.</summary>
    public Action<string>? OpenQueryEditor { get; init; }
}
