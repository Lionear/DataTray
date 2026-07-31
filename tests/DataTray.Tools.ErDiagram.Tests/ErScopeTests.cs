using DataTray.Plugins.Schema;
using DataTray.Tools.ErDiagram;

namespace DataTray.Tools.ErDiagram.Tests;

/// <summary>
/// SE-217: "+ Related". The mockup calls this the feature that makes the diagram usable on a real
/// database, so what it pulls in is worth pinning precisely.
/// </summary>
public class ErScopeTests
{
    private static TableDef Table(string name, params (string Column, string RefTable)[] foreignKeys) =>
        new(
            Schema: "public",
            Name: name,
            Columns: [new ColumnDef("id", "int", false, null, 0)],
            PrimaryKey: new PrimaryKeyDef($"pk_{name}", ["id"]),
            Indexes: [],
            ForeignKeys: foreignKeys
                .Select(fk => new ForeignKeyDef($"fk_{name}", [fk.Column], "public", fk.RefTable, ["id"]))
                .ToList(),
            Uniques: []);

    // The mockup's example: tick orders, press + Related, and customers and order_items come along.
    private static readonly TableDef[] Shop =
    [
        Table("customers"),
        Table("products"),
        Table("orders", ("customer_id", "customers")),
        Table("order_items", ("order_id", "orders"), ("product_id", "products")),
        Table("audit_log"),
    ];

    [Fact]
    public void One_hop_follows_foreign_keys_in_both_directions()
    {
        var grown = ErScope.ExpandOneHop(Shop, ["public.orders"]);

        Assert.Equal(
            ["public.customers", "public.order_items", "public.orders"],
            grown.OrderBy(k => k).ToArray());
    }

    [Fact]
    public void An_unrelated_table_is_not_pulled_in()
    {
        var grown = ErScope.ExpandOneHop(Shop, ["public.orders"]);

        Assert.DoesNotContain("public.audit_log", grown);
    }

    [Fact]
    public void It_is_one_hop_and_not_the_whole_component()
    {
        // customers is two hops from order_items (order_items -> orders -> customers). Reaching it in one
        // press would make the button "select everything connected", which is a different feature.
        var grown = ErScope.ExpandOneHop(Shop, ["public.order_items"]);

        Assert.Contains("public.orders", grown);
        Assert.Contains("public.products", grown);
        Assert.DoesNotContain("public.customers", grown);
    }

    [Fact]
    public void Pressing_it_again_grows_another_ring()
    {
        // Which is how a user walks outward at their own pace instead of choosing a depth up front.
        var once = ErScope.ExpandOneHop(Shop, ["public.order_items"]);
        var twice = ErScope.ExpandOneHop(Shop, once.ToList());

        Assert.Contains("public.customers", twice);
    }

    [Fact]
    public void The_selection_only_ever_grows()
    {
        var grown = ErScope.ExpandOneHop(Shop, ["public.audit_log"]);

        Assert.Contains("public.audit_log", grown);
        Assert.Single(grown);
    }

    [Fact]
    public void An_empty_selection_stays_empty()
    {
        Assert.Empty(ErScope.ExpandOneHop(Shop, []));
    }

    [Fact]
    public void A_self_reference_pulls_in_nothing_new()
    {
        TableDef[] tables = [Table("employees", ("manager_id", "employees"))];

        Assert.Single(ErScope.ExpandOneHop(tables, ["public.employees"]));
    }
}
