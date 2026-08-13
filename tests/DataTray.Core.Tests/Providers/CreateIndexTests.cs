using DataTray.Sdk;
using DataTray.Sdk.Ddl;
using DataTray.Sdk.Schema;
using DataTray.Providers.MsSql;
using DataTray.Providers.MySql;
using DataTray.Providers.Postgres;

namespace DataTray.Core.Tests.Providers;

/// <summary>
/// "New Index…" (SE-250) across the providers that declare it. The statement is the same shape everywhere;
/// what differs is quoting and whether the table is qualified with a schema, and those are exactly the two
/// things a shared builder could get wrong for three engines while looking right for the fourth.
/// </summary>
public class CreateIndexTests
{
    private static CreateObjectSpec Spec(bool unique = false, params NewIndexColumnSpec[] columns) =>
        new(DbObjectKind.Index, "IX_Fitting_Name", "app", [], "Fitting",
            columns.Length > 0 ? columns : [new NewIndexColumnSpec("Name", false)], unique);

    [Fact]
    public void SqlServer_qualifies_the_table_with_its_schema_and_brackets_every_name()
    {
        var sql = new MsSqlProvider().BuildCreateStatement(Spec()).Text;

        Assert.Equal("CREATE INDEX [IX_Fitting_Name] ON [app].[Fitting] ([Name])", sql);
    }

    [Fact]
    public void Postgres_qualifies_the_table_and_double_quotes_every_name()
    {
        var sql = new PostgresProvider().BuildCreateStatement(Spec()).Text;

        Assert.Equal("CREATE INDEX \"IX_Fitting_Name\" ON \"app\".\"Fitting\" (\"Name\")", sql);
    }

    [Fact]
    public void MySql_names_the_table_alone_because_it_has_no_schema_layer()
    {
        // The connection is already pointed at the database, as it is for CREATE TABLE. Qualifying with
        // the schema here would produce a two-part name MySQL reads as database.table — the wrong database.
        var sql = new MySqlProvider().BuildCreateStatement(Spec()).Text;

        Assert.Equal("CREATE INDEX `IX_Fitting_Name` ON `Fitting` (`Name`)", sql);
    }

    [Fact]
    public void Unique_is_spelled_the_same_way_everywhere()
    {
        Assert.StartsWith("CREATE UNIQUE INDEX", new MsSqlProvider().BuildCreateStatement(Spec(unique: true)).Text);
        Assert.StartsWith("CREATE UNIQUE INDEX", new PostgresProvider().BuildCreateStatement(Spec(unique: true)).Text);
        Assert.StartsWith("CREATE UNIQUE INDEX", new MySqlProvider().BuildCreateStatement(Spec(unique: true)).Text);
    }

    [Fact]
    public void Key_order_is_kept_and_only_descending_columns_are_called_out()
    {
        // ASC is the default everywhere, so spelling it out on every column would bury the one that isn't.
        var sql = new MsSqlProvider().BuildCreateStatement(Spec(
            false,
            new NewIndexColumnSpec("Slot", true),
            new NewIndexColumnSpec("Name", false))).Text;

        Assert.Equal("CREATE INDEX [IX_Fitting_Name] ON [app].[Fitting] ([Slot] DESC, [Name])", sql);
    }

    [Fact]
    public void An_index_without_columns_is_refused_before_it_reaches_the_server()
    {
        var spec = new CreateObjectSpec(DbObjectKind.Index, "IX_Empty", "app", [], "Fitting", [], false);

        Assert.Throws<InvalidOperationException>(() => new MsSqlProvider().BuildCreateStatement(spec));
    }

    [Fact]
    public void An_index_without_a_table_is_refused()
    {
        var spec = new CreateObjectSpec(DbObjectKind.Index, "IX_Orphan", "app", [], null,
            [new NewIndexColumnSpec("Name", false)], false);

        Assert.Throws<InvalidOperationException>(() => new MsSqlProvider().BuildCreateStatement(spec));
    }

    [Fact]
    public void Every_provider_that_can_create_an_index_offers_it_on_the_Indexes_folder()
    {
        // The menu item is gated on this pairing; declaring the capability under the wrong node kind would
        // hide the feature completely while every other part of it worked.
        foreach (IDbProvider provider in new IDbProvider[]
                 {
                     new MsSqlProvider(), new PostgresProvider(), new MySqlProvider()
                 })
        {
            var capability = provider.CreateCapabilities.SingleOrDefault(c => c.Kind == DbObjectKind.Index);

            Assert.NotNull(capability);
            Assert.Equal(DbNodeKind.IndexFolder, capability!.ParentNode);
        }
    }
}
