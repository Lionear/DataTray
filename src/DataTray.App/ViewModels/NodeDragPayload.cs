using System.Text.Json;
using Avalonia.Input;
using DataTray.Sdk;
using DataTray.Sdk.Extensibility;
using DataTray.Sdk.Schema;

namespace DataTray.App.ViewModels;

/// <summary>What a table/view node carries when dragged out of the tree (SE-299 H5): the node as JSON for
/// panes, and its qualified name as plain text for the editor and anything else that takes text.</summary>
public sealed record NodeDragPayload(string Json, string Text)
{
    /// <summary>In-process: the drag never leaves the app, and an application format cannot carry the
    /// '/' in <see cref="DraggedNode.Format"/>. Panes read it with the same call.</summary>
    public static readonly DataFormat<string> NodeFormat = DataFormat.CreateInProcessFormat<string>(DraggedNode.Format);

    public static NodeDragPayload? For(TreeNodeViewModel node, ISqlDialect dialect)
    {
        if (!node.IsTableOrView)
        {
            return null;
        }

        var dragged = new DraggedNode(node.Connection.Id, node.DatabaseName, node.SchemaName, node.Name,
            node.NodeKind == DbNodeKind.View);
        return new NodeDragPayload(JsonSerializer.Serialize(dragged),
            dialect.QualifyName(node.DatabaseName, node.SchemaName, node.Name));
    }

    public DataTransfer ToDataTransfer()
    {
        var item = new DataTransferItem();
        item.Set(NodeFormat, Json);
        item.SetText(Text);
        var transfer = new DataTransfer();
        transfer.Add(item);
        return transfer;
    }
}
