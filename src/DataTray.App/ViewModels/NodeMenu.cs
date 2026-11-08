using DataTray.Sdk.Extensibility;

namespace DataTray.App.ViewModels;

/// <summary>Plugin items for a table/view node's context menu (SE-299 H5).</summary>
public static class NodeMenu
{
    /// <summary>The target and the contributions that apply to it; null for any node but a table or view.
    /// The selected tab is the active document only when it is a query tab on the node's connection.</summary>
    public static (NodeMenuTarget Target, IReadOnlyList<NodeMenuContribution> Items)? For(
        TreeNodeViewModel node,
        DocumentViewModel? selected,
        IEnumerable<NodeMenuContribution> contributions,
        Action<string> report)
    {
        if (!node.IsTableOrView)
        {
            return null;
        }

        var c = node.Connection;
        var active = selected is { IsQueryMode: true } document && document.Connection?.Id == c.Id ? selected : null;
        var target = new NodeMenuTarget(
            new ManagedConnectionInfo(c.Id, c.Name, c.ProviderId, c.Folder, c.Values),
            node.DatabaseName, node.SchemaName, node.Name, node.NodeKind == Sdk.Schema.DbNodeKind.View, active);

        var items = new List<NodeMenuContribution>();
        foreach (var contribution in contributions)
        {
            // AppliesTo is plugin code on the UI thread: one that throws costs its own item, not the menu.
            try
            {
                if (contribution.AppliesTo(target))
                {
                    items.Add(contribution);
                }
            }
            catch (Exception ex)
            {
                report($"{contribution.Id}: {ex.Message}");
            }
        }

        return (target, items);
    }

    /// <summary>Run a contribution; a throw is reported, never rethrown (the caller is an async-void click).</summary>
    public static async Task InvokeAsync(NodeMenuContribution item, NodeMenuTarget target, IHostUi hostUi, Action<string> report)
    {
        try
        {
            await item.InvokeAsync(target, hostUi);
        }
        catch (Exception ex)
        {
            report($"{item.Id}: {ex.Message}");
        }
    }
}
