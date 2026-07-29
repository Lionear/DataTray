using DataTray.Providers.ClickHouse;
using DataTray.Sdk;
using DataTray.Sdk.Connections;
using DataTray.Sdk.Ddl;
using DataTray.Sdk.Schema;

namespace DataTray.Core.Tests.Providers;

// The pure, server-free parts of the ClickHouse provider (SE-36): the DDL it generates and its dialect
// rules. Both differ from every other SQL provider in ways worth pinning — a mandatory table engine, an
// inverted nullability default, and a "primary key" that is a sorting key rather than a constraint.
public class ClickHouseProviderTests
{
    private static readonly ClickHouseProvider Provider = new();

    [Fact] // A table engine is mandatory in ClickHouse, and MergeTree demands an ORDER BY.
    public void Create_table_declares_MergeTree_ordered_by_the_primary_key()
    {
        var spec = new CreateObjectSpec(DbObjectKind.Table, "events", null,
        [
            new NewColumnSpec("id", "UInt64", Nullable: false, PrimaryKey: true, AutoIncrement: false),
            new NewColumnSpec("at", "DateTime", Nullable: false, PrimaryKey: true, AutoIncrement: false)
        ]);

        var sql = Provider.BuildCreateStatement(spec).Text;

        Assert.Contains("CREATE TABLE `events`", sql);
        Assert.Contains("ENGINE = MergeTree", sql);
        Assert.Contains("ORDER BY (`id`, `at`)", sql);
    }

    [Fact] // No primary key still needs an ORDER BY: tuple() is ClickHouse's explicit "no sorting key".
    public void Create_table_without_a_primary_key_orders_by_an_empty_tuple()
    {
        var spec = new CreateObjectSpec(DbObjectKind.Table, "log", null,
            [new NewColumnSpec("line", "String", Nullable: false, PrimaryKey: false, AutoIncrement: false)]);

        Assert.Contains("ORDER BY tuple()", Provider.BuildCreateStatement(spec).Text);
    }

    [Fact] // Inverted from SQL: a ClickHouse column is NOT NULL unless its type is wrapped in Nullable(…),
           // so nullability is expressed in the type rather than as a NOT NULL suffix.
    public void Nullable_columns_are_wrapped_in_the_Nullable_type()
    {
        var spec = new CreateObjectSpec(DbObjectKind.Table, "t", null,
        [
            new NewColumnSpec("a", "String", Nullable: true, PrimaryKey: false, AutoIncrement: false),
            new NewColumnSpec("b", "String", Nullable: false, PrimaryKey: false, AutoIncrement: false),
            // Already wrapped by the user — must not become Nullable(Nullable(String)).
            new NewColumnSpec("c", "Nullable(String)", Nullable: true, PrimaryKey: false, AutoIncrement: false)
        ]);

        var sql = Provider.BuildCreateStatement(spec).Text;

        Assert.Contains("`a` Nullable(String)", sql);
        Assert.Contains("`b` String", sql);
        Assert.Contains("`c` Nullable(String)", sql);
        Assert.DoesNotContain("Nullable(Nullable(", sql);
        Assert.DoesNotContain("NOT NULL", sql);
    }

    [Fact] // AutoIncrement has no ClickHouse equivalent; it is dropped rather than rendered as something else.
    public void AutoIncrement_is_ignored()
    {
        var spec = new CreateObjectSpec(DbObjectKind.Table, "t", null,
            [new NewColumnSpec("id", "UInt64", Nullable: false, PrimaryKey: true, AutoIncrement: true)]);

        var sql = Provider.BuildCreateStatement(spec).Text;

        Assert.Contains("`id` UInt64", sql);
        Assert.DoesNotContain("AUTO", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IDENTITY", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_database_needs_no_engine()
    {
        var spec = new CreateObjectSpec(DbObjectKind.Database, "analytics", null, []);

        Assert.Equal("CREATE DATABASE `analytics`", Provider.BuildCreateStatement(spec).Text);
    }

    [Fact]
    public void Schemas_are_not_creatable_because_the_engine_has_no_schema_layer()
    {
        Assert.DoesNotContain(Provider.CreateCapabilities, c => c.Kind == DbObjectKind.Schema);

        Assert.Throws<NotSupportedException>(() =>
            Provider.BuildCreateStatement(new CreateObjectSpec(DbObjectKind.Schema, "s", null, [])));
    }

    [Fact] // Backticks, doubling an embedded one — and a two-part `db`.`table`, since there is no schema layer.
    public void Dialect_quotes_with_backticks_and_qualifies_in_two_parts()
    {
        var dialect = Provider.Dialect;

        Assert.Equal("`odd``name`", dialect.QuoteIdentifier("odd`name"));
        Assert.Equal("`demo`.`events`", dialect.QualifyName("demo", null, "events"));
        Assert.Equal("`demo`.`events`", dialect.QualifyName("demo", "ignored", "events"));
        Assert.Equal("`events`", dialect.QualifyName(null, null, "events"));
    }

    [Fact]
    public void Dialect_pages_with_limit_offset()
    {
        Assert.Equal(
            "SELECT * FROM t\nORDER BY `id` DESC\nLIMIT 50 OFFSET 100",
            Provider.Dialect.Paginate("SELECT * FROM t", 50, 100, "`id` DESC"));
    }

    [Fact] // ClickHouse is a real SQL engine, so the host's SQL scaffolds stay on — unlike Mongo/Elasticsearch.
           // Read through IDbProvider: these are default interface members the provider leaves alone.
    public void Is_sql_based_and_exposes_the_server_capabilities()
    {
        IDbProvider provider = Provider;

        Assert.True(provider.IsSqlBased);
        Assert.True(provider.SupportsActivityMonitor);
        Assert.True(provider.CanManageUsers);
        Assert.Equal("query_id", provider.SessionIdColumn);

        // KILL QUERY is ClickHouse's only kill verb — the HTTP interface has no session to terminate — so
        // the soft-cancel action stays off rather than duplicating it.
        Assert.False(provider.SupportsCancelQuery);

        // No stored routines or triggers exist in ClickHouse, so the routine flow stays unreachable.
        Assert.Throws<NotSupportedException>(() =>
            provider.BuildCallStatement([], [], new Dictionary<string, string?>()));
    }

    [Fact] // Only tables and views have a SHOW CREATE definition; anything else returns null, not an error.
    public async Task Object_definition_is_null_for_a_node_that_has_none()
    {
        var definition = await Provider.GetObjectDefinitionAsync(
            new ConnectionProfile { Name = "ch", ConnectionString = "Host=localhost" },
            [new DbNodeRef(DbNodeKind.Database, "demo"), new DbNodeRef(DbNodeKind.Column, "id")],
            CancellationToken.None);

        Assert.Null(definition);
    }
}
