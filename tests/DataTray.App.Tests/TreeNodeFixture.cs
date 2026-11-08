using DataTray.App.ViewModels;
using DataTray.Core.Connections;
using DataTray.Sdk.Schema;

namespace DataTray.App.Tests;

/// <summary>Builds a loaded tree Prod › Sales › dbo › {customers (table), active_customers (view)}.
/// Needs <see cref="HeadlessDrawing"/>: node icons parse geometry.</summary>
internal static class TreeNodeFixture
{
    // Children by depth of the path being expanded: connection, database, schema.
    private static readonly IReadOnlyList<DbTreeNode>[] ChildrenByDepth =
    [
        [new DbTreeNode { Kind = DbNodeKind.Database, Name = "Sales", HasChildren = true }],
        [new DbTreeNode { Kind = DbNodeKind.Schema, Name = "dbo", HasChildren = true }],
        [new DbTreeNode { Kind = DbNodeKind.Table, Name = "customers" },
         new DbTreeNode { Kind = DbNodeKind.View, Name = "active_customers" }],
    ];

    public static TreeNodeViewModel Node(string name) => Schema().Children.First(c => c.Name == name);

    public static TreeNodeViewModel Schema()
    {
        var connection = new SavedConnection
        {
            Id = "c1", Name = "Prod", ProviderId = "fake", Values = new Dictionary<string, string?>(),
        };
        var root = TreeNodeViewModel.ForConnection(connection, new TreeStubProvider(), iconImage: null,
            (_, path) => Task.FromResult(ChildrenByDepth[path.Count]));
        root.IsExpanded = true;
        var database = root.Children.Single();
        database.IsExpanded = true;
        var schema = database.Children.Single();
        schema.IsExpanded = true;
        return schema;
    }
}
