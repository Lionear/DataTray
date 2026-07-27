using DataTray.Sdk;
using DataTray.Sdk.Query;
using DataTray.Sdk.Scripting;

namespace DataTray.Core.Tests.Scripting;

public class SqlValueLiteralTests
{
    [Fact]
    public void Null_and_dbnull_render_as_NULL()
    {
        Assert.Equal("NULL", SqlValueLiteral.Format(null, SqlLiteralDialect.Generic));
        Assert.Equal("NULL", SqlValueLiteral.Format(DBNull.Value, SqlLiteralDialect.Generic));
    }

    [Fact]
    public void Numbers_render_verbatim_and_culture_invariant()
    {
        Assert.Equal("42", SqlValueLiteral.Format(42, SqlLiteralDialect.Generic));
        Assert.Equal("-7", SqlValueLiteral.Format(-7L, SqlLiteralDialect.Generic));
        Assert.Equal("3.14", SqlValueLiteral.Format(3.14m, SqlLiteralDialect.Generic));
    }

    [Fact]
    public void Strings_are_quoted_and_single_quotes_doubled()
    {
        Assert.Equal("'O''Brien'", SqlValueLiteral.Format("O'Brien", SqlLiteralDialect.Generic));
        Assert.Equal("'plain'", SqlValueLiteral.Format("plain", SqlLiteralDialect.Generic));
    }

    [Theory]
    [InlineData(SqlLiteralDialect.SqlServer, "1", "0")]
    [InlineData(SqlLiteralDialect.Sqlite, "1", "0")]
    [InlineData(SqlLiteralDialect.Postgres, "TRUE", "FALSE")]
    [InlineData(SqlLiteralDialect.MySql, "TRUE", "FALSE")]
    public void Booleans_follow_the_dialect(SqlLiteralDialect dialect, string t, string f)
    {
        Assert.Equal(t, SqlValueLiteral.Format(true, dialect));
        Assert.Equal(f, SqlValueLiteral.Format(false, dialect));
    }

    [Fact]
    public void Binary_uses_the_dialect_specific_literal()
    {
        var bytes = new byte[] { 0xDE, 0xAD };
        Assert.Equal("0xDEAD", SqlValueLiteral.Format(bytes, SqlLiteralDialect.SqlServer));
        Assert.Equal("'\\xDEAD'", SqlValueLiteral.Format(bytes, SqlLiteralDialect.Postgres));
        Assert.Equal("X'DEAD'", SqlValueLiteral.Format(bytes, SqlLiteralDialect.MySql));
    }

    [Fact]
    public void Dates_are_quoted_iso()
    {
        Assert.Equal("'2026-07-21'", SqlValueLiteral.Format(new DateTime(2026, 7, 21), SqlLiteralDialect.Generic));
        Assert.Equal("'2026-07-21 09:30:00.000'",
            SqlValueLiteral.Format(new DateTime(2026, 7, 21, 9, 30, 0), SqlLiteralDialect.Generic));
    }

    [Theory]
    [InlineData("sqlserver", SqlLiteralDialect.SqlServer)]
    [InlineData("postgres", SqlLiteralDialect.Postgres)]
    [InlineData("mysql", SqlLiteralDialect.MySql)]
    [InlineData("sqlite", SqlLiteralDialect.Sqlite)]
    [InlineData("dragonflydb", SqlLiteralDialect.Generic)]
    [InlineData(null, SqlLiteralDialect.Generic)]
    public void DialectFor_maps_provider_ids(string? providerId, SqlLiteralDialect expected) =>
        Assert.Equal(expected, SqlValueLiteral.DialectFor(providerId));
}

public class InsertScripterTests
{
    // Minimal quoting dialect — double-quote identifiers, enough to exercise the scripter.
    private sealed class FakeDialect : ISqlDialect
    {
        public IReadOnlySet<string> Keywords => new HashSet<string>();
        public string QuoteIdentifier(string identifier) => "\"" + identifier + "\"";
        public string QualifyName(string? database, string? schema, string table) => table;
        public string Paginate(string sql, int limit, int offset, string? orderBy = null) => sql;
    }

    private static ResultColumn Col(string name, bool readOnly = false) =>
        new(name, typeof(object)) { BaseColumn = name, IsReadOnly = readOnly };

    [Fact]
    public void Builds_one_insert_per_row_skipping_readonly_columns()
    {
        var columns = new[] { Col("id", readOnly: true), Col("name"), Col("active") };
        var rows = new object?[][]
        {
            new object?[] { 1, "Ann", true },
            new object?[] { 2, "O'Neil", null }
        };

        var sql = InsertScripter.Build("t", columns, rows, new FakeDialect(), SqlLiteralDialect.Postgres);

        // "id" is read-only → excluded from both the column list and the values.
        Assert.Contains("INSERT INTO t (\"name\", \"active\") VALUES ('Ann', TRUE);", sql);
        Assert.Contains("INSERT INTO t (\"name\", \"active\") VALUES ('O''Neil', NULL);", sql);
        Assert.Equal(2, sql.Split("INSERT INTO", StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void Empty_rows_produce_a_commented_template_not_statements()
    {
        var sql = InsertScripter.Build("t", new[] { Col("a") }, [], new FakeDialect(), SqlLiteralDialect.Generic);

        Assert.Contains("No rows to script", sql);
        // No executable statement — every line is a comment.
        Assert.All(sql.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            line => Assert.StartsWith("--", line.TrimStart()));
    }

    [Fact]
    public void No_writable_columns_is_reported()
    {
        var sql = InsertScripter.Build("t", new[] { Col("id", readOnly: true) },
            new object?[][] { new object?[] { 1 } }, new FakeDialect(), SqlLiteralDialect.Generic);

        Assert.Contains("No insertable columns", sql);
    }
}
