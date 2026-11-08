namespace DataTray.Sdk.Extensibility;

/// <summary>
/// Optional contribution to add items to table/view nodes' context menus in the tree, gated by the
/// <see cref="PluginCapabilities.Menu"/> capability.
/// </summary>
public interface INodeMenuPlugin
{
    /// <summary>The node-context-menu items this plugin contributes.</summary>
    IReadOnlyList<NodeMenuContribution> NodeMenuItems { get; }
}
