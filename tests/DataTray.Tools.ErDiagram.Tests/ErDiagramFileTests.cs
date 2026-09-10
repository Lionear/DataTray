using DataTray.Plugins.Schema;
using DataTray.Tools.ErDiagram;

namespace DataTray.Tools.ErDiagram.Tests;

/// <summary>
/// SE-225. The saved file records which tables you drew and nothing about what is in them — that absence
/// is the design, not an omission, so it is worth a test of its own.
/// </summary>
public class ErDiagramFileTests
{
    private static TableDef Table(string name) =>
        new("public", name,
            [new ColumnDef("id", "int", false, null, 0), new ColumnDef("secret", "text", true, null, 1)],
            new PrimaryKeyDef("pk", ["id"]), [], [], []);

    private static readonly TableDef[] Live = [Table("customers"), Table("orders")];

    private static ErDiagramFile Saved(params string[] tables) => new()
    {
        ProviderId = "postgres",
        ConnectionName = "Prod",
        Database = "shop",
        Tables = tables,
    };

    [Fact]
    public void A_saved_diagram_round_trips()
    {
        var file = Saved("public.customers", "public.orders");

        var back = ErDiagramFile.FromJson(file.ToJson());

        Assert.Equal(file.ProviderId, back.ProviderId);
        Assert.Equal(file.ConnectionName, back.ConnectionName);
        Assert.Equal(file.Database, back.Database);
        Assert.Equal(file.Tables, back.Tables);
    }

    [Fact]
    public void It_stores_no_schema_detail()
    {
        // The line that keeps this inside SE-82's Model A decision: with no columns or types in the file
        // there is no second version of the truth, so nothing to diff and nothing to synchronise.
        var json = Saved("public.customers").ToJson();

        Assert.DoesNotContain("secret", json);
        Assert.DoesNotContain("int", json);
        Assert.DoesNotContain("column", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Opening_it_draws_the_tables_that_still_exist()
    {
        var resolved = Saved("public.customers", "public.orders").ResolveAgainst(Live);

        Assert.Equal(["public.customers", "public.orders"], resolved.Present);
        Assert.Empty(resolved.Missing);
    }

    [Fact]
    public void A_table_that_has_gone_is_reported_not_dropped()
    {
        // A picture that silently draws two of your three tables is worse than no picture — and a table
        // disappearing is exactly what someone opens an old diagram to find out.
        var resolved = Saved("public.customers", "public.legacy_orders").ResolveAgainst(Live);

        Assert.Equal(["public.customers"], resolved.Present);
        Assert.Equal(["public.legacy_orders"], resolved.Missing);
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        var resolved = Saved("PUBLIC.CUSTOMERS").ResolveAgainst(Live);

        Assert.Single(resolved.Present);
        Assert.Empty(resolved.Missing);
    }

    [Fact]
    public void A_newer_format_is_refused_with_a_sentence_rather_than_half_read()
    {
        var json = """{"schemaVersion": 99, "providerId": "postgres", "tables": ["public.customers"]}""";

        var error = Assert.Throws<InvalidDataException>(() => ErDiagramFile.FromJson(json));
        Assert.Contains("newer version", error.Message);
    }

    [Fact]
    public void Broken_json_fails_with_something_worth_showing()
    {
        var error = Assert.Throws<InvalidDataException>(() => ErDiagramFile.FromJson("{not json"));

        Assert.NotEmpty(error.Message);
    }

    [Fact]
    public void An_older_format_number_is_accepted()
    {
        // Forward compatibility is refused; backward compatibility is the whole point of the field.
        var json = """{"schemaVersion": 1, "providerId": "sqlite", "tables": ["orders"]}""";

        Assert.Equal("sqlite", ErDiagramFile.FromJson(json).ProviderId);
    }
}
