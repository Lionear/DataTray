using DataTray.App.ViewModels;
using DataTray.Core.Connections;
using DataTray.Sdk;
using DataTray.Sdk.Branding;
using DataTray.Sdk.Connections;
using DataTray.Sdk.Ddl;
using DataTray.Sdk.Query;
using DataTray.Sdk.Schema;

namespace DataTray.App.Tests;

// SE-285. The whole point of the filter is which rows survive it, so that is what these assert:
// the hit, the path down to it, and nothing else.
public class SidebarTreeFilterTests : IClassFixture<HeadlessDrawing>
{
    [Fact]
    public void Keeps_a_hit_its_ancestors_and_its_own_subtree_and_hides_the_rest()
    {
        var root = BuildLoadedTree();
        var sales = Child(root, "SalesDb");
        var order = Child(sales, "Order");

        var (matches, loaded) = SidebarTreeFilter.Apply([root], "order");

        Assert.Equal(2, matches);                              // Order + OrderLine
        Assert.Equal(6, loaded);                               // root + 2 databases + 3 tables
        Assert.True(root.IsFilterVisible);                     // ancestor: shows WHERE the hit lives
        Assert.True(sales.IsFilterVisible);
        Assert.True(order.IsFilterVisible);
        Assert.True(Child(sales, "OrderLine").IsFilterVisible);
        Assert.False(Child(sales, "Customer").IsFilterVisible); // no hit above or below it
        Assert.False(Child(root, "WarehouseDb").IsFilterVisible);
    }

    [Fact] // A hit you found stays browsable: its children are not filtered out from under it.
    public void Everything_under_a_hit_stays_visible()
    {
        var root = BuildLoadedTree();
        var sales = Child(root, "SalesDb");

        SidebarTreeFilter.Apply([root], "SalesDb");

        Assert.True(sales.IsFilterVisible);
        Assert.True(Child(sales, "Customer").IsFilterVisible);
    }

    [Fact] // Nothing matches -> the connection root itself disappears, which is what filtering a list means.
    public void Hides_a_connection_whose_name_and_loaded_children_do_not_match()
    {
        var root = BuildLoadedTree();

        SidebarTreeFilter.Apply([root], "zzz");

        Assert.False(root.IsFilterVisible);
    }

    [Fact] // Clearing the box must put back everything the filter hid, at every depth.
    public void Reveal_undoes_the_filter()
    {
        var root = BuildLoadedTree();
        SidebarTreeFilter.Apply([root], "order");

        SidebarTreeFilter.Reveal([root]);

        Assert.True(Child(root, "WarehouseDb").IsFilterVisible);
        Assert.True(Child(Child(root, "SalesDb"), "Customer").IsFilterVisible);
    }

    [Fact] // SE-285 mockup: the row shows WHICH part matched, so a substring hit is readable at a glance.
    public void A_visible_row_is_split_around_the_match_and_put_back_together_when_cleared()
    {
        var root = BuildLoadedTree();
        var orderLine = Child(Child(root, "SalesDb"), "OrderLine");

        SidebarTreeFilter.Apply([root], "derl"); // mid-word, and a different case than the title

        Assert.Equal("Or", orderLine.TitleBefore);
        Assert.Equal("derL", orderLine.TitleMatch); // the title's own casing, not the query's
        Assert.Equal("ine", orderLine.TitleAfter);

        // A row that survives on an ancestor's match has nothing highlighted of its own.
        Assert.Equal(root.Title, root.TitleBefore);
        Assert.Empty(root.TitleMatch);

        SidebarTreeFilter.Reveal([root]);
        Assert.Equal("OrderLine", orderLine.TitleBefore);
        Assert.Empty(orderLine.TitleMatch);
        Assert.Empty(orderLine.TitleAfter);
    }

    [Fact] // Never search — or hide — what nobody has fetched: the count says "loaded" for a reason.
    public void An_unexpanded_branch_is_not_loaded_so_it_is_not_counted()
    {
        var root = BuildRoot();
        root.IsExpanded = true; // one level only; WarehouseDb keeps its "…" placeholder

        var (_, loaded) = SidebarTreeFilter.Apply([root], "order");

        Assert.Equal(3, loaded); // root + 2 databases, none of the tables underneath
    }

    // A connection root with SalesDb (Order/OrderLine/Customer) and WarehouseDb, both levels expanded.
    // Expanding runs the loader below, which completes synchronously.
    private static TreeNodeViewModel BuildLoadedTree()
    {
        var root = BuildRoot();
        root.IsExpanded = true;
        foreach (var database in root.Children)
        {
            database.IsExpanded = true;
        }

        return root;
    }

    private static TreeNodeViewModel BuildRoot()
    {
        var provider = new StubProvider();
        var connection = new SavedConnection
        {
            Id = "c1", Name = "Productie", ProviderId = "fake", Values = new Dictionary<string, string?>(),
        };

        return TreeNodeViewModel.ForConnection(connection, provider, iconImage: null, Load);
    }

    private static Task<IReadOnlyList<DbTreeNode>> Load(SavedConnection connection, IReadOnlyList<DbNodeRef> path)
    {
        IReadOnlyList<DbTreeNode> children = path.Count switch
        {
            0 => [Node("SalesDb", DbNodeKind.Database, hasChildren: true),
                  Node("WarehouseDb", DbNodeKind.Database, hasChildren: true)],
            1 when path[0].Name == "SalesDb" =>
                [Node("Order", DbNodeKind.Table), Node("OrderLine", DbNodeKind.Table), Node("Customer", DbNodeKind.Table)],
            _ => [],
        };

        return Task.FromResult(children);
    }

    private static DbTreeNode Node(string name, DbNodeKind kind, bool hasChildren = false) =>
        new() { Kind = kind, Name = name, HasChildren = hasChildren };

    private static TreeNodeViewModel Child(TreeNodeViewModel parent, string name) =>
        parent.Children.First(c => c.Name == name);

    // Only ever asked for its identity here — the tree filter reads names, not the database.
    private sealed class StubProvider : IDbProvider
    {
        public string DisplayName => "Fake DB";
        public ProviderIcon? Icon => null;
        public ISqlDialect Dialect => throw new NotSupportedException();
        public bool IsSqlBased => true;
        public IReadOnlyList<ConnectionField> ConnectionFields => [];
        public string BuildConnectionString(IReadOnlyDictionary<string, string?> values) => "fake";
        public Task<bool> TestConnectionAsync(ConnectionProfile profile, CancellationToken ct) => Task.FromResult(true);
        public Task<IReadOnlyList<DbTreeNode>> GetChildNodesAsync(ConnectionProfile profile, IReadOnlyList<DbNodeRef> ancestors, CancellationToken ct) => throw new NotSupportedException();
        public Task<QueryResult> ExecuteQueryAsync(ConnectionProfile profile, string sql, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> ExecuteBatchAsync(ConnectionProfile profile, IReadOnlyList<SqlStatement> statements, CancellationToken ct) => throw new NotSupportedException();
        public IReadOnlyList<CreateCapability> CreateCapabilities => [];
        public IReadOnlyList<string> ColumnTypes => [];
        public SqlStatement BuildCreateStatement(CreateObjectSpec spec) => throw new NotSupportedException();
        public Task ExecuteDdlAsync(ConnectionProfile profile, string sql, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetDatabasesAsync(ConnectionProfile profile, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<QueryResult>> ExecuteScriptAsync(ConnectionProfile profile, string sql, CancellationToken ct) => throw new NotSupportedException();
        public Task<QueryResult> ExplainAsync(ConnectionProfile profile, string sql, CancellationToken ct) => throw new NotSupportedException();
    }
}
