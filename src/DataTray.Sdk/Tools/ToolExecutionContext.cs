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
public sealed record ToolExecutionContext(
    ConnectionProfile Profile,
    DbNodeRef? Node,
    IDbProvider Provider,
    string ProviderId,
    IToolHost Host,
    IPluginLocalizer Localizer);
