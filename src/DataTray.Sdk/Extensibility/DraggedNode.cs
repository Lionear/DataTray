namespace DataTray.Sdk.Extensibility;

/// <summary>Payload of a tree node dragged out of the tree, carried under <see cref="Format"/>.</summary>
public sealed record DraggedNode(string ConnectionId, string? Database, string? Schema, string Name, bool IsView)
{
    /// <summary>Drag-and-drop data format the host uses for a dragged node.</summary>
    public const string Format = "application/x-datatray-node";
}
