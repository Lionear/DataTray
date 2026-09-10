using DataTray.Sdk.Connections;
using DataTray.Sdk.Localization;
using DataTray.Sdk.Schema;

namespace DataTray.Sdk.Tools;

/// <summary>
/// Everything a tool needs to run against the selected connection/node. <see cref="Provider"/> (not just
/// its dialect) is handed over so a generic tool can walk the schema, run queries and recreate objects
/// through the same interfaces the host uses — the "universal" tools rely on this. <see cref="Node"/> is
/// the tree node the tool was launched on, or null when launched on the connection root.
/// </summary>
/// <param name="Localizer">The plugin's localizer for runtime text (errors, progress). Never null — the
/// host supplies <see cref="EmptyPluginLocalizer.Instance"/> when the plugin ships no translations, so a
/// tool can always write <c>context.Localizer["key"]</c> without a null check.</param>
/// <param name="NodePath">The path from the connection root down to <see cref="Node"/>, inclusive — the
/// same ancestry a provider already receives for introspection and for
/// <see cref="IDbProvider.BuildDropUserStatement"/>. A name alone does not identify every node: an index
/// is named within its table and an "Indexes" folder is named nothing at all, so a tool acting on one
/// cannot know what it is acting on without this. Empty for a connection root, and empty for a host older
/// than tool API v7, so a tool that reads it should say what is missing rather than guess.</param>
public sealed record ToolExecutionContext(
    ConnectionProfile Profile,
    DbNodeRef? Node,
    IDbProvider Provider,
    string ProviderId,
    IToolHost Host,
    IPluginLocalizer Localizer,
    IReadOnlyList<DbNodeRef>? NodePath = null)
{
    public IReadOnlyList<DbNodeRef> NodePath { get; init; } = NodePath ?? [];

    /// <summary>The name of the nearest ancestor of <paramref name="kind"/> on the way down to this node
    /// (the node itself included), or null when there is none — how a tool asks "which table is this index
    /// on?" without walking the list itself.</summary>
    public string? Ancestor(DbNodeKind kind) =>
        NodePath.LastOrDefault(n => n.Kind == kind)?.Name;
}
