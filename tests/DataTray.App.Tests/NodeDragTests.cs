using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using AvaloniaEdit;
using DataTray.App.ViewModels;
using DataTray.App.Views;
using DataTray.Sdk.Extensibility;

namespace DataTray.App.Tests;

/// <summary>Dragging a table/view out of the tree (SE-299 H5).</summary>
public class NodeDragTests : IClassFixture<HeadlessDrawing>
{
    [Theory]
    [InlineData("customers", false)]
    [InlineData("active_customers", true)]
    public void DragPayload_TableOrView_CarriesNodeJsonAndQualifiedText(string name, bool isView)
    {
        var payload = NodeDragPayload.For(TreeNodeFixture.Node(name), new RecordingDialect());

        Assert.NotNull(payload);
        Assert.Equal(new DraggedNode("c1", "Sales", "dbo", name, isView), JsonSerializer.Deserialize<DraggedNode>(payload.Json));
        Assert.Equal($"Sales|dbo|{name}", payload.Text);
    }

    [Fact]
    public void DragPayload_SchemaNode_IsNull()
    {
        Assert.Null(NodeDragPayload.For(TreeNodeFixture.Schema(), new RecordingDialect()));
    }

    [Fact]
    public void DropNode_OnEditor_InsertsQualifiedName()
    {
        var sql = HeadlessDrawing.Run(() =>
        {
            var (document, editor) = EditorView();
            Drop(editor, Payload());
            return document.Sql;
        });

        Assert.Equal("Sales|dbo|customers", sql);
    }

    // Drops a node payload onto the editor the way the platform does: enter, over, drop.
    internal static void Drop(TextEditor editor, NodeDragPayload payload)
    {
        var transfer = payload.ToDataTransfer();
        Raise(editor, transfer, DragDrop.DragEnterEvent);
        Raise(editor, transfer, DragDrop.DragOverEvent);
        Raise(editor, transfer, DragDrop.DropEvent);
    }

    private static void Raise(TextEditor editor, IDataTransfer transfer, Avalonia.Interactivity.RoutedEvent<DragEventArgs> routed) =>
        editor.TextArea.RaiseEvent(new DragEventArgs(routed, transfer, editor.TextArea, new Point(1, 1), KeyModifiers.None)
        {
            DragEffects = DragDropEffects.Copy,
        });

    internal static NodeDragPayload Payload() =>
        NodeDragPayload.For(TreeNodeFixture.Node("customers"), new RecordingDialect())
            ?? throw new InvalidOperationException("no payload");

    private static (DocumentViewModel Document, TextEditor Editor) EditorView()
    {
        LinkedEditorViewTests.SeedAppResources();
        var document = QueryDocumentFixture.NewDocument(out var connections, QueryDocumentFixture.Ready("master"));
        document.InitQuery(connections.List().First(c => c.Id == "c1"));
        var view = new DocumentView { DataContext = document };
        var window = new Window { Content = view };
        window.Show();
        window.UpdateLayout();
        var editor = view.FindControl<TextEditor>("SqlEditor") ?? throw new InvalidOperationException("no SqlEditor");
        return (document, editor);
    }
}
