using DataTray.Plugins.Schema;
using DataTray.Tools.ErDiagram;

namespace DataTray.Tools.ErDiagram.Tests;

public class ErLayoutTests
{
    private static readonly LayeredErLayout Layout = new();

    private static TableDef Table(string name, params (string Column, string RefTable)[] foreignKeys) =>
        new(
            Schema: "public",
            Name: name,
            Columns: [new ColumnDef("id", "int", false, null, 0)],
            PrimaryKey: new PrimaryKeyDef($"pk_{name}", ["id"]),
            Indexes: [],
            ForeignKeys: foreignKeys
                .Select(fk => new ForeignKeyDef($"fk_{name}_{fk.Column}", [fk.Column], "public", fk.RefTable, ["id"]))
                .ToList(),
            Uniques: []);

    private static Dictionary<string, ErPlacement> Compute(params TableDef[] tables) =>
        Layout.Compute(ErGraph.Build(tables)).Placements.ToDictionary(p => p.Key);

    [Fact]
    public void A_table_with_no_foreign_keys_sits_at_rank_zero()
    {
        var placed = Compute(Table("categories"), Table("customers"));

        Assert.Equal(0, placed["public.categories"].Rank);
        Assert.Equal(0, placed["public.customers"].Rank);
    }

    [Fact]
    public void Rank_is_one_past_the_deepest_table_referenced()
    {
        // The mockup's shape: categories ← products ← order_items, with orders in between.
        var placed = Compute(
            Table("categories"),
            Table("customers"),
            Table("products", ("category_id", "categories")),
            Table("orders", ("customer_id", "customers")),
            Table("order_items", ("order_id", "orders"), ("product_id", "products")));

        Assert.Equal(0, placed["public.categories"].Rank);
        Assert.Equal(0, placed["public.customers"].Rank);
        Assert.Equal(1, placed["public.products"].Rank);
        Assert.Equal(1, placed["public.orders"].Rank);
        Assert.Equal(2, placed["public.order_items"].Rank);
    }

    [Fact]
    public void The_mockup_schema_lays_out_the_way_the_mockup_draws_it()
    {
        // The approved mockup (Depot: mockups/se-82-er-diagram.html) hand-positions these six tables in
        // three columns. This is the check that the engine actually produces that picture, rather than
        // something merely defensible — the mockup is what was signed off.
        var placed = Compute(
            Table("categories"),
            Table("customers"),
            Table("products", ("category_id", "categories")),
            Table("orders", ("customer_id", "customers")),
            Table("addresses", ("customer_id", "customers")),
            Table("order_items", ("order_id", "orders"), ("product_id", "products")));

        string[] Rank(int rank) => placed.Values
            .Where(p => p.Rank == rank)
            .OrderBy(p => p.Order)
            .Select(p => p.Key)
            .ToArray();

        Assert.Equal(["public.categories", "public.customers"], Rank(0));
        Assert.Equal(3, Rank(1).Length);
        Assert.Equal(["public.addresses", "public.orders", "public.products"], Rank(1).Order().ToArray());
        Assert.Equal(["public.order_items"], Rank(2));
    }

    [Fact]
    public void A_self_reference_is_drawn_but_does_not_affect_the_rank()
    {
        // employees.manager_id -> employees.id. Ranking it as a dependency would make the table depend
        // on itself; the relation still has to reach the canvas.
        var employees = Table("employees", ("manager_id", "employees"));
        var graph = ErGraph.Build([employees]);

        Assert.Single(graph.Edges);
        Assert.True(graph.Edges[0].IsSelfReference);
        Assert.Equal(0, Layout.Compute(graph).Placements.Single().Rank);
    }

    [Fact]
    public void A_cycle_between_two_tables_terminates_and_places_both()
    {
        // Mutual references are legal schema design and have no dependency order to reflect. The only
        // requirement is that it finishes and puts both boxes somewhere.
        var placed = Compute(
            Table("employees", ("department_id", "departments")),
            Table("departments", ("head_id", "employees")));

        Assert.Equal(2, placed.Count);
        Assert.All(placed.Values, p => Assert.True(p.Rank >= 0));
    }

    [Fact]
    public void A_longer_cycle_terminates()
    {
        var placed = Compute(
            Table("a", ("b_id", "b")),
            Table("b", ("c_id", "c")),
            Table("c", ("a_id", "a")));

        Assert.Equal(3, placed.Count);
    }

    [Fact]
    public void A_cycle_with_a_tail_still_ranks_the_tail_behind_it()
    {
        // The cycle's own order is arbitrary, but a table hanging off it must still land to its right.
        var placed = Compute(
            Table("a", ("b_id", "b")),
            Table("b", ("a_id", "a")),
            Table("log", ("a_id", "a")));

        Assert.True(placed["public.log"].Rank > placed["public.a"].Rank);
    }

    [Fact]
    public void A_foreign_key_to_a_table_outside_the_scope_is_counted_not_drawn()
    {
        // The picker draws a subset, so this is the normal case, not an error.
        var graph = ErGraph.Build([Table("orders", ("customer_id", "customers"))]);

        Assert.Empty(graph.Edges);
        Assert.Equal(1, graph.RelationsOutOfScope);
    }

    [Fact]
    public void An_empty_scope_produces_an_empty_layout()
    {
        var result = Layout.Compute(ErGraph.Build([]));

        Assert.Empty(result.Placements);
        Assert.Equal(0, result.RankCount);
    }

    [Fact]
    public void The_layout_is_identical_across_runs_and_input_orders()
    {
        // A diagram that reshuffles itself on every open cannot be read for its shape.
        TableDef[] tables =
        [
            Table("categories"),
            Table("customers"),
            Table("products", ("category_id", "categories")),
            Table("orders", ("customer_id", "customers")),
            Table("order_items", ("order_id", "orders"), ("product_id", "products")),
        ];

        var first = Layout.Compute(ErGraph.Build(tables)).Placements;
        var reversed = Layout.Compute(ErGraph.Build(tables.Reverse().ToArray())).Placements;

        Assert.Equal(
            first.Select(p => (p.Key, p.Rank, p.Order)),
            reversed.Select(p => (p.Key, p.Rank, p.Order)));
    }

    [Fact]
    public void Within_a_rank_every_table_gets_a_distinct_slot()
    {
        var placed = Compute(
            Table("a"), Table("b"), Table("c"),
            Table("x", ("a_id", "a")), Table("y", ("b_id", "b")), Table("z", ("c_id", "c")));

        foreach (var rank in placed.Values.GroupBy(p => p.Rank))
        {
            var orders = rank.Select(p => p.Order).ToList();
            Assert.Equal(orders.Count, orders.Distinct().Count());
            Assert.Equal(Enumerable.Range(0, orders.Count), orders.OrderBy(o => o));
        }
    }

    [Fact]
    public void A_dependent_is_placed_near_what_it_references()
    {
        // Barycentre ordering: each dependent should end up beside its own parent rather than in
        // alphabetical order, which would put z at the top and cross every edge.
        var placed = Compute(
            Table("a"), Table("b"), Table("c"),
            Table("z_of_a", ("a_id", "a")), Table("y_of_b", ("b_id", "b")), Table("x_of_c", ("c_id", "c")));

        var parents = new[] { "public.a", "public.b", "public.c" }.Select(k => placed[k].Order).ToList();
        var children = new[] { "public.z_of_a", "public.y_of_b", "public.x_of_c" }
            .Select(k => placed[k].Order).ToList();

        Assert.Equal(parents, children);
    }

    [Fact]
    public void An_empty_ref_schema_resolves_against_the_referencing_table()
    {
        // SQLite has no schemas, and the other readers leave RefSchema empty for a same-schema reference.
        var orders = new TableDef(
            Schema: "public",
            Name: "orders",
            Columns: [new ColumnDef("id", "int", false, null, 0)],
            PrimaryKey: null,
            Indexes: [],
            ForeignKeys: [new ForeignKeyDef("fk", ["customer_id"], RefSchema: "", "customers", ["id"])],
            Uniques: []);

        var graph = ErGraph.Build([orders, Table("customers")]);

        Assert.Single(graph.Edges);
        Assert.Equal(0, graph.RelationsOutOfScope);
    }

    [Fact]
    public void Two_foreign_keys_to_the_same_table_are_two_relations_but_one_dependency()
    {
        // shipments.origin_id and shipments.destination_id both -> addresses.
        var placed = Compute(
            Table("addresses"),
            Table("shipments", ("origin_id", "addresses"), ("destination_id", "addresses")));

        Assert.Equal(1, placed["public.shipments"].Rank);

        var graph = ErGraph.Build([Table("addresses"), Table("shipments", ("origin_id", "addresses"), ("destination_id", "addresses"))]);
        Assert.Equal(2, graph.Edges.Count);
    }
}
