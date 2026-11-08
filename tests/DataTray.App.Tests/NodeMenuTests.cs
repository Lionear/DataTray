using DataTray.App.ViewModels;
using DataTray.Sdk.Extensibility;

namespace DataTray.App.Tests;

/// <summary>Plugin items on a table/view node's context menu (SE-299 H5).</summary>
public class NodeMenuTests : IClassFixture<HeadlessDrawing>
{
    [Fact]
    public void NodeMenu_TableNode_ListsApplicableContributions_WithActiveDocument()
    {
        var node = TreeNodeFixture.Node("customers");
        var document = QueryTab("c1");
        var applies = Contribution("open", _ => true);

        var menu = NodeMenu.For(node, document, [applies, Contribution("other", _ => false)], _ => { });

        Assert.NotNull(menu);
        var target = menu.Value.Target;
        Assert.Equal(("c1", "Sales", "dbo", "customers", false),
            (target.Connection.Id, target.Database, target.Schema, target.Name, target.IsView));
        Assert.Same(document, target.ActiveDocument);
        Assert.Equal([applies], menu.Value.Items);
    }

    [Fact] // A tab on another connection is not the context a plugin should act on.
    public void NodeMenu_QueryTabOnOtherConnection_HasNoActiveDocument()
    {
        var menu = NodeMenu.For(TreeNodeFixture.Node("customers"), QueryTab("c2"), [], _ => { });

        Assert.Null(menu?.Target.ActiveDocument);
        Assert.NotNull(menu);
    }

    [Fact] // AppliesTo is plugin code: one that throws costs its own item, not the menu.
    public void NodeMenu_ThrowingAppliesTo_DropsItemAndReports()
    {
        var reported = new List<string>();
        var applies = Contribution("ok", _ => true);

        var menu = NodeMenu.For(TreeNodeFixture.Node("customers"), null,
            [Contribution("bad", _ => throw new InvalidOperationException("boom")), applies], reported.Add);

        Assert.Equal([applies], menu?.Items);
        Assert.Equal(["bad: boom"], reported);
    }

    [Fact]
    public void NodeMenu_SchemaNode_IsNull()
    {
        Assert.Null(NodeMenu.For(TreeNodeFixture.Schema(), null, [Contribution("open", _ => true)], _ => { }));
    }

    private static DocumentViewModel QueryTab(string connectionId)
    {
        var document = QueryDocumentFixture.NewDocument(out var connections, QueryDocumentFixture.Ready("master"));
        document.InitQuery(connections.List().First(c => c.Id == connectionId));
        return document;
    }

    private static NodeMenuContribution Contribution(string id, Func<NodeMenuTarget, bool> appliesTo) =>
        new(id, id, appliesTo, (_, _) => Task.CompletedTask);
}
