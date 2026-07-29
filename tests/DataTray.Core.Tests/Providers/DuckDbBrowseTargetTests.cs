using DataTray.Providers.DuckDb;

namespace DataTray.Core.Tests.Providers;

// DuckDB.NET's reader reports no base-table or primary-key metadata, so the DuckDB provider recovers the
// table from the query text to decide whether a grid may be editable (SE-12). Getting that wrong in the
// permissive direction would let the host generate an UPDATE against the wrong table, so these pin both
// halves: what must match, and — more importantly — what must not.
public class DuckDbBrowseTargetTests
{
    [Theory] // The shapes the host's own browse/paging/filter/sort generation produces.
    [InlineData("SELECT * FROM people", null, "people")]
    [InlineData("SELECT * FROM \"people\"", null, "people")]
    [InlineData("SELECT * FROM \"main\".\"people\"", "main", "people")]
    [InlineData("SELECT * FROM main.people", "main", "people")]
    [InlineData("SELECT * FROM \"main\" . \"people\"", "main", "people")]
    [InlineData("select * from People", null, "People")]
    [InlineData("SELECT * FROM people;", null, "people")]
    [InlineData("  SELECT *\nFROM people\n", null, "people")]
    [InlineData("SELECT id, name FROM people", null, "people")]
    [InlineData("SELECT * FROM people WHERE score > 1", null, "people")]
    [InlineData("SELECT * FROM people ORDER BY \"id\" DESC", null, "people")]
    [InlineData("SELECT * FROM people\nORDER BY \"id\" DESC\nLIMIT 50 OFFSET 100", null, "people")]
    [InlineData("SELECT * FROM people LIMIT 1000", null, "people")]
    public void Recovers_the_table_of_a_single_table_select(string sql, string? schema, string table)
    {
        var target = DuckDbBrowseTarget.From(sql);

        Assert.NotNull(target);
        Assert.Equal(schema, target!.Schema);
        Assert.Equal(table, target.Table);
    }

    [Fact] // An embedded double quote is doubled in DuckDB, and must survive the round trip unescaped.
    public void Unescapes_a_doubled_quote_in_an_identifier()
    {
        var target = DuckDbBrowseTarget.From("""SELECT * FROM "odd""name" """);

        Assert.Equal("odd\"name", target!.Table);
    }

    [Theory] // Nothing here maps one grid row onto one stored row, so all of these must stay read-only.
    [InlineData("SELECT count(*) FROM people")]                                  // aggregate (also: parens)
    [InlineData("SELECT p.id FROM people p JOIN other q ON p.id = q.id")]        // join
    [InlineData("SELECT * FROM people UNION SELECT * FROM other")]               // set operation
    [InlineData("SELECT * FROM people INTERSECT SELECT * FROM other")]
    [InlineData("SELECT * FROM people EXCEPT SELECT * FROM other")]
    [InlineData("SELECT DISTINCT name FROM people")]                             // distinct
    [InlineData("SELECT name FROM people GROUP BY name")]                        // grouping
    [InlineData("SELECT name FROM people GROUP BY name HAVING count(*) > 1")]
    [InlineData("SELECT * FROM (SELECT * FROM people)")]                         // subquery
    [InlineData("SELECT * FROM people, other")]                                  // implicit cross join
    [InlineData("SELECT row_number() OVER () FROM people")]                      // window function
    [InlineData("SELECT * FROM people QUALIFY row_number() OVER () = 1")]
    [InlineData("WITH x AS (SELECT 1) SELECT * FROM x")]                         // CTE — does not start at SELECT
    [InlineData("UPDATE people SET name = 'x'")]                                 // not a SELECT at all
    [InlineData("INSERT INTO people VALUES (1)")]
    [InlineData("PRAGMA table_info('people')")]
    [InlineData("")]
    [InlineData("   ")]
    public void Refuses_anything_that_is_not_one_tables_rows(string sql) =>
        Assert.Null(DuckDbBrowseTarget.From(sql));

    [Fact] // Null in, null out — the provider calls this with whatever text the user ran.
    public void Refuses_null() => Assert.Null(DuckDbBrowseTarget.From(null));

    [Theory] // DuckDB's headline feature reads files as tables. There is no table to write back to, and a
             // path is not an identifier, so these must be read-only rather than "a table called out.parquet".
    [InlineData("SELECT * FROM 'events.parquet'")]
    [InlineData("SELECT * FROM read_parquet('events.parquet')")]
    [InlineData("SELECT * FROM read_csv_auto('data.csv')")]
    [InlineData("SELECT * FROM range(5)")]
    [InlineData("SELECT * FROM glob('*.parquet')")]
    public void Refuses_a_file_or_table_function(string sql) =>
        Assert.Null(DuckDbBrowseTarget.From(sql));

    [Fact] // A comment must not be able to invent a table…
    public void Ignores_a_table_named_only_in_a_comment()
    {
        Assert.Equal("people", DuckDbBrowseTarget.From("SELECT * FROM people -- FROM other")!.Table);
        Assert.Equal("people", DuckDbBrowseTarget.From("/* FROM other */ SELECT * FROM people")!.Table);
        Assert.Equal("people", DuckDbBrowseTarget.From("SELECT * FROM people /* JOIN other */")!.Table);
    }

    [Fact] // …and a string literal must not be mistaken for one, nor its contents for a comment.
    public void Treats_a_string_literal_as_opaque()
    {
        // The literal contains what would otherwise read as a line comment; the statement is still simple.
        Assert.Equal("people", DuckDbBrowseTarget.From("SELECT * FROM people WHERE name = '-- x'")!.Table);
        // A literal mentioning JOIN is disqualifying only because of the parens rule, not the word — but a
        // plain literal must leave a simple statement matchable.
        Assert.Equal("people", DuckDbBrowseTarget.From("SELECT * FROM people WHERE name = 'a;b'")!.Table);
    }
}
