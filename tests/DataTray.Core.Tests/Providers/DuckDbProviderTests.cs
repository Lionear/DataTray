using DataTray.Providers.DuckDb;
using DataTray.Sdk;
using DataTray.Sdk.Connections;
using DataTray.Sdk.Ddl;
using DataTray.Sdk.Schema;

namespace DataTray.Core.Tests.Providers;

// The parts of the DuckDB provider that can be tested without the engine (SE-12): the DDL it generates and
// its dialect rules.
//
// Connection-string building is deliberately NOT covered here, and the reason is worth recording:
// DuckDBConnectionStringBuilder is not pure managed code. Setting DataSource routes through its
// set_Item, which validates the keyword against the native library's option list, so it throws
// DllNotFoundException/NullReferenceException when libduckdb is absent. The test project excludes DuckDB's
// ~316 MB of per-RID natives on purpose, so those paths are covered end-to-end against a real database
// instead (both modes, including the ":memory:" round trip).
public class DuckDbProviderTests
{
    private static readonly DuckDbProvider Provider = new();

    [Fact]
    public void Create_table_is_schema_qualified_with_a_primary_key()
    {
        var spec = new CreateObjectSpec(DbObjectKind.Table, "people", "main",
        [
            new NewColumnSpec("id", "INTEGER", Nullable: false, PrimaryKey: true, AutoIncrement: false),
            new NewColumnSpec("name", "VARCHAR", Nullable: true, PrimaryKey: false, AutoIncrement: false)
        ]);

        var sql = Provider.BuildCreateStatement(spec).Text;

        Assert.Contains("CREATE TABLE \"main\".\"people\"", sql);
        Assert.Contains("\"id\" INTEGER NOT NULL", sql);
        Assert.Contains("\"name\" VARCHAR", sql);
        Assert.Contains("PRIMARY KEY (\"id\")", sql);
        Assert.DoesNotContain("CREATE SEQUENCE", sql);
    }

    [Fact] // DuckDB has no AUTO_INCREMENT/IDENTITY keyword; the documented pattern is a sequence plus a
           // DEFAULT that draws from it, so the generated script is two statements.
    public void Auto_increment_becomes_a_sequence_and_a_default()
    {
        var spec = new CreateObjectSpec(DbObjectKind.Table, "people", "main",
            [new NewColumnSpec("id", "INTEGER", Nullable: false, PrimaryKey: true, AutoIncrement: true)]);

        var sql = Provider.BuildCreateStatement(spec).Text;

        Assert.StartsWith("CREATE SEQUENCE \"main\".\"seq_people_id\";", sql);
        Assert.Contains("DEFAULT nextval('\"main\".\"seq_people_id\"')", sql);
        Assert.DoesNotContain("AUTOINCREMENT", sql);
        Assert.DoesNotContain("IDENTITY", sql);
    }

    [Fact]
    public void Create_schema_is_supported_but_create_database_is_not()
    {
        Assert.Equal("CREATE SCHEMA \"analytics\"",
            Provider.BuildCreateStatement(new CreateObjectSpec(DbObjectKind.Schema, "analytics", null, [])).Text);

        // A second DuckDB file is ATTACHed into the session, never created, so the host offers no
        // "New Database" for this engine.
        Assert.DoesNotContain(Provider.CreateCapabilities, c => c.Kind == DbObjectKind.Database);
        Assert.Throws<NotSupportedException>(() =>
            Provider.BuildCreateStatement(new CreateObjectSpec(DbObjectKind.Database, "db", null, [])));
    }

    [Fact] // Postgres/ANSI double quotes, and a two-part schema.table — the catalog stays implicit because
           // one connection is one file.
    public void Dialect_quotes_with_double_quotes_and_ignores_the_catalog()
    {
        var dialect = Provider.Dialect;

        Assert.Equal("\"odd\"\"name\"", dialect.QuoteIdentifier("odd\"name"));
        Assert.Equal("\"main\".\"people\"", dialect.QualifyName(null, "main", "people"));
        // The database argument is accepted and deliberately dropped.
        Assert.Equal("\"main\".\"people\"", dialect.QualifyName("analytics.duckdb", "main", "people"));
        Assert.Equal("\"people\"", dialect.QualifyName(null, null, "people"));
    }

    [Fact]
    public void Dialect_pages_with_limit_offset() =>
        Assert.Equal(
            "SELECT * FROM t\nORDER BY \"id\" DESC\nLIMIT 50 OFFSET 100",
            Provider.Dialect.Paginate("SELECT * FROM t", 50, 100, "\"id\" DESC"));

    [Fact] // An embedded engine has no server to containerise, no catalogs to switch between, and no
           // sessions to monitor — the same answers the file-based SQLite provider gives.
    public async Task Has_no_server_shaped_capabilities()
    {
        IDbProvider provider = Provider;

        Assert.Null(provider.ContainerRecipe);
        Assert.False(provider.SupportsActivityMonitor);
        Assert.False(provider.CanManageUsers);
        Assert.True(provider.IsSqlBased);
        Assert.Empty(await provider.GetDatabasesAsync(
            new ConnectionProfile { Name = "d", ConnectionString = "DataSource=:memory:" }, CancellationToken.None));
    }

    [Fact] // Only tables and views have a definition to show; anything else returns null rather than erroring.
    public async Task Object_definition_is_null_for_a_node_that_has_none()
    {
        var definition = await Provider.GetObjectDefinitionAsync(
            new ConnectionProfile { Name = "d", ConnectionString = "DataSource=:memory:" },
            [new DbNodeRef(DbNodeKind.Schema, "main"), new DbNodeRef(DbNodeKind.Column, "id")],
            CancellationToken.None);

        Assert.Null(definition);
    }
}
