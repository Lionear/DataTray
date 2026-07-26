using SqlExplorer.Plugins.Schema;
using SqlExplorer.Tools.CopyTable;

namespace SqlExplorer.Tools.CopyTable.Tests;

public class CreateTableWriterTests
{
    private static CreateTableWriter Writer(string providerId) => new(SqlDialect.For(providerId));

    private static TableDef Table(
        string schema,
        string name,
        IReadOnlyList<ColumnDef> cols,
        PrimaryKeyDef? pk = null,
        IReadOnlyList<IndexDef>? indexes = null,
        IReadOnlyList<ForeignKeyDef>? foreignKeys = null,
        IReadOnlyList<UniqueDef>? uniques = null) =>
        new(schema, name, cols, pk, indexes ?? [], foreignKeys ?? [], uniques ?? []);

    private static ColumnDef Col(
        string name, string type, bool nullable = true, string? def = null, int ord = 0, bool identity = false) =>
        new(name, type, nullable, def, ord, identity);

    [Theory]
    [InlineData("postgres", "\"id\"")]
    [InlineData("sqlite", "\"id\"")]
    [InlineData("mysql", "`id`")]
    [InlineData("sqlserver", "[id]")]
    public void Quote_follows_the_engine(string providerId, string expected) =>
        Assert.Equal(expected, Writer(providerId).Quote("id"));

    [Fact]
    public void Quote_escapes_the_delimiter()
    {
        Assert.Equal("[a]]b]", Writer("sqlserver").Quote("a]b"));
        Assert.Equal("`a``b`", Writer("mysql").Quote("a`b"));
        Assert.Equal("\"a\"\"b\"", Writer("postgres").Quote("a\"b"));
    }

    [Fact]
    public void Unqualified_when_schema_is_empty()
    {
        var t = Table("", "t", [Col("a", "int")]);
        Assert.Equal("\"t\"", Writer("postgres").QuoteTable(t));
    }

    [Fact]
    public void Renders_columns_nullability_default_and_named_primary_key()
    {
        var t = Table("public", "person",
        [
            Col("id", "integer", nullable: false, ord: 1),
            Col("name", "character varying(100)", nullable: false, ord: 2),
            Col("note", "text", nullable: true, ord: 3),
            Col("active", "boolean", nullable: false, def: "true", ord: 4)
        ],
            new PrimaryKeyDef("person_pkey", ["id"]));

        var sql = Writer("postgres").Build(t, keepIdentity: true, includeForeignKeys: true);

        Assert.Contains("CREATE TABLE \"public\".\"person\" (", sql);
        Assert.Contains("\"id\" integer NOT NULL", sql);
        Assert.Contains("\"name\" character varying(100) NOT NULL", sql);
        Assert.Contains("\"note\" text", sql);
        Assert.DoesNotContain("\"note\" text NOT NULL", sql);
        Assert.Contains("\"active\" boolean NOT NULL DEFAULT true", sql);
        Assert.Contains("CONSTRAINT \"person_pkey\" PRIMARY KEY (\"id\")", sql);
        Assert.EndsWith(");", sql);
    }

    [Fact]
    public void MySql_primary_key_is_unnamed_because_every_one_of_them_is_called_PRIMARY()
    {
        var t = Table("", "t", [Col("a", "int", nullable: false, ord: 1)], new PrimaryKeyDef("PRIMARY", ["a"]));
        var sql = Writer("mysql").Build(t, keepIdentity: true, includeForeignKeys: true);
        Assert.Contains("PRIMARY KEY (`a`)", sql);
        Assert.DoesNotContain("CONSTRAINT", sql);
    }

    [Fact]
    public void Columns_render_in_ordinal_order()
    {
        var t = Table("s", "t",
        [
            Col("c", "int", ord: 3),
            Col("a", "int", ord: 1),
            Col("b", "int", ord: 2)
        ]);
        var sql = Writer("mysql").Build(t, keepIdentity: true, includeForeignKeys: true);
        Assert.True(sql.IndexOf("`a`", StringComparison.Ordinal) < sql.IndexOf("`b`", StringComparison.Ordinal));
        Assert.True(sql.IndexOf("`b`", StringComparison.Ordinal) < sql.IndexOf("`c`", StringComparison.Ordinal));
    }

    [Fact]
    public void Keep_identity_renders_a_plain_column_and_drops_the_sequence_default()
    {
        // A Postgres serial reads as integer with a nextval default; keeping the values means a plain column so
        // the original ids insert cleanly, with the sequence default dropped.
        var t = Table("public", "t",
        [
            Col("id", "integer", nullable: false, def: "nextval('t_id_seq'::regclass)", ord: 1, identity: true),
            Col("name", "text", ord: 2)
        ]);

        var sql = Writer("postgres").Build(t, keepIdentity: true, includeForeignKeys: true);

        Assert.Contains("\"id\" integer NOT NULL", sql);
        Assert.DoesNotContain("nextval", sql);
        Assert.DoesNotContain("serial", sql);
        Assert.DoesNotContain("IDENTITY", sql);
        Assert.DoesNotContain("GENERATED", sql);
    }

    [Theory]
    [InlineData("postgres", "\"id\" serial")]
    [InlineData("sqlserver", "[id] int IDENTITY(1,1) NOT NULL")]
    [InlineData("mysql", "`id` int NOT NULL AUTO_INCREMENT")]
    public void Regenerate_identity_adds_the_engine_clause(string providerId, string expected)
    {
        var t = Table("dbo", "t",
        [
            Col("id", "int", nullable: false, def: "nextval('x')", ord: 1, identity: true),
            Col("name", "varchar(50)", ord: 2)
        ]);

        var sql = Writer(providerId).Build(t, keepIdentity: false, includeForeignKeys: true);

        Assert.Contains(expected, sql);
        // The sequence default is never emitted for an identity column, either way.
        Assert.DoesNotContain("nextval", sql);
    }

    [Fact]
    public void ColumnsForInsert_keeps_all_when_keeping_identity_and_drops_it_otherwise()
    {
        var t = Table("s", "t",
        [
            Col("id", "int", nullable: false, ord: 1, identity: true),
            Col("name", "text", ord: 2)
        ]);

        Assert.Equal(["id", "name"], CreateTableWriter.ColumnsForInsert(t, keepIdentity: true).Select(c => c.Name));
        Assert.Equal(["name"], CreateTableWriter.ColumnsForInsert(t, keepIdentity: false).Select(c => c.Name));
    }

    [Theory]
    [InlineData("postgres", "DROP TABLE IF EXISTS \"public\".\"t\";")]
    [InlineData("sqlserver", "DROP TABLE IF EXISTS [public].[t];")]
    [InlineData("mysql", "DROP TABLE IF EXISTS `public`.`t`;")]
    public void DropIfExists_quotes_for_the_engine(string providerId, string expected)
    {
        var t = Table("public", "t", [Col("a", "int")]);
        Assert.Equal(expected, Writer(providerId).DropIfExists(t));
    }

    [Fact]
    public void Table_without_a_primary_key_has_no_primary_key_clause()
    {
        var t = Table("s", "t", [Col("a", "int", ord: 1)]);
        var sql = Writer("postgres").Build(t, keepIdentity: true, includeForeignKeys: true);
        Assert.DoesNotContain("PRIMARY KEY", sql);
    }

    // ── Indexes, uniques and foreign keys (SE-192 §1) ────────────────────────────────────────────────────

    private static TableDef Orders() => Table("public", "orders",
        [Col("id", "int", nullable: false, ord: 1), Col("customer_id", "int", ord: 2), Col("code", "text", ord: 3)],
        new PrimaryKeyDef("orders_pkey", ["id"]),
        indexes: [new IndexDef("ix_orders_customer", Unique: false, ["customer_id"])],
        foreignKeys: [new ForeignKeyDef("fk_orders_customer", ["customer_id"], "public", "customers", ["id"])],
        uniques: [new UniqueDef("uq_orders_code", ["code"])]);

    [Fact]
    public void Unique_constraints_are_always_inline_in_the_create()
    {
        var sql = Writer("postgres").Build(Orders(), keepIdentity: true, includeForeignKeys: false);
        Assert.Contains("CONSTRAINT \"uq_orders_code\" UNIQUE (\"code\")", sql);
    }

    [Fact]
    public void Indexes_are_separate_statements()
    {
        var indexes = Writer("postgres").Indexes(Orders());
        Assert.Equal(
            ["CREATE INDEX \"ix_orders_customer\" ON \"public\".\"orders\" (\"customer_id\");"],
            indexes);
    }

    [Fact]
    public void Unique_index_keeps_its_keyword()
    {
        var t = Table("s", "t", [Col("a", "int")], indexes: [new IndexDef("ix", Unique: true, ["a"])]);
        Assert.Contains("CREATE UNIQUE INDEX", Writer("postgres").Indexes(t)[0]);
    }

    [Fact]
    public void Foreign_keys_are_added_after_the_fact_on_an_engine_that_can_alter_constraints()
    {
        var writer = Writer("sqlserver");
        var t = Orders();

        Assert.DoesNotContain("FOREIGN KEY", writer.Build(t, keepIdentity: true, includeForeignKeys: true));
        Assert.Equal(
            ["ALTER TABLE [public].[orders] ADD CONSTRAINT [fk_orders_customer] " +
             "FOREIGN KEY ([customer_id]) REFERENCES [public].[customers] ([id]);"],
            writer.ForeignKeys(t));
    }

    [Fact]
    public void Sqlite_declares_foreign_keys_inline_because_it_cannot_add_them_later()
    {
        // SQLite's ALTER TABLE can't add a constraint at all, so a foreign key not written into the CREATE
        // could never be created — and there is nothing left to run afterwards.
        var writer = Writer("sqlite");
        // SQLite has no schemas, so its reader leaves the schema empty on both sides of a reference.
        var t = Table("", "orders",
            [Col("id", "INTEGER", nullable: false, ord: 1), Col("customer_id", "INTEGER", ord: 2)],
            new PrimaryKeyDef("pk_orders", ["id"]),
            foreignKeys: [new ForeignKeyDef("fk_orders_0", ["customer_id"], "", "customers", ["id"])]);

        var sql = writer.Build(t, keepIdentity: true, includeForeignKeys: true);

        Assert.Contains(
            "CONSTRAINT \"fk_orders_0\" FOREIGN KEY (\"customer_id\") REFERENCES \"customers\" (\"id\")",
            sql);
        Assert.Empty(writer.ForeignKeys(t));
    }

    [Fact]
    public void Sqlite_leaves_the_foreign_keys_out_when_the_copy_excludes_them()
    {
        var sql = Writer("sqlite").Build(Orders(), keepIdentity: true, includeForeignKeys: false);
        Assert.DoesNotContain("FOREIGN KEY", sql);
    }

    [Fact]
    public void Sqlite_identity_is_the_integer_primary_key_itself_not_a_clause()
    {
        // An INTEGER PRIMARY KEY is the rowid, so SQLite fills it in with no clause to declare — and
        // AUTOINCREMENT is only legal inline, which the table-level PRIMARY KEY(...) rendering isn't.
        var t = Table("", "t",
            [Col("id", "INTEGER", nullable: false, ord: 1, identity: true)],
            new PrimaryKeyDef("pk_t", ["id"]));

        var sql = Writer("sqlite").Build(t, keepIdentity: false, includeForeignKeys: false);

        Assert.Contains("\"id\" INTEGER NOT NULL", sql);
        Assert.Contains("PRIMARY KEY (\"id\")", sql);
        Assert.DoesNotContain("AUTOINCREMENT", sql);
    }
}
