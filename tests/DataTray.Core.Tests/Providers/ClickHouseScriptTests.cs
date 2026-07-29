using DataTray.Providers.ClickHouse;

namespace DataTray.Core.Tests.Providers;

// ClickHouse's HTTP interface rejects a multi-statement request ("Code: 62 … Multi-statements are not
// allowed"), so its provider is the only one that has to split script text itself before sending one
// request per statement. These pin the splitting rules; the cases below were all verified against a real
// 26.3 server while the provider was written (SE-36).
public class ClickHouseScriptTests
{
    [Theory]
    [InlineData("SELECT 1", 1)]
    [InlineData("SELECT 1;", 1)]                         // a trailing ; is not an empty second statement
    [InlineData("SELECT 1; SELECT 2", 2)]
    [InlineData("SELECT 1; SELECT 2;", 2)]
    [InlineData(";;;", 0)]                               // separators only — nothing to run
    [InlineData("", 0)]
    [InlineData("   \n\t ", 0)]
    public void Counts_top_level_statements(string text, int expected) =>
        Assert.Equal(expected, ClickHouseScript.Split(text).Count);

    [Theory]
    [InlineData("SELECT 'a;b'")]                         // ; inside a string literal
    [InlineData(@"SELECT 'a\'b;c'")]                     // ClickHouse escapes with a backslash, not by doubling
    [InlineData("SELECT `we;ird`")]                      // ; inside a backtick-quoted identifier
    [InlineData("SELECT \"we;ird\"")]                    // …or a double-quoted one
    [InlineData("SELECT 1 -- ;\nAND 1")]                 // ; inside a -- line comment
    [InlineData("SELECT 1 # ;\nAND 1")]                  // …or a # line comment (ClickHouse accepts both)
    [InlineData("/* ; */ SELECT 1")]                     // ; inside a block comment
    public void Ignores_a_semicolon_that_is_not_a_separator(string text) =>
        Assert.Single(ClickHouseScript.Split(text));

    [Fact]
    public void Trims_each_statement_and_drops_the_separator()
    {
        var statements = ClickHouseScript.Split("  SELECT 1 ;\n\n  SELECT 2  ;  ");

        Assert.Equal(["SELECT 1", "SELECT 2"], statements);
    }

    [Fact] // The realistic case: DDL, DML and a query pasted into one tab.
    public void Splits_a_mixed_ddl_dml_script()
    {
        var statements = ClickHouseScript.Split("""
            DROP TABLE IF EXISTS demo.events;
            CREATE TABLE demo.events (id UInt64, label String) ENGINE = MergeTree ORDER BY id;
            INSERT INTO demo.events VALUES (1, 'a'), (2, 'b;c');
            SELECT count() FROM demo.events;
            """);

        Assert.Equal(4, statements.Count);
        Assert.StartsWith("DROP TABLE", statements[0]);
        Assert.Contains("ENGINE = MergeTree", statements[1]);
        Assert.EndsWith("'b;c')", statements[2]);
        Assert.Equal("SELECT count() FROM demo.events", statements[3]);
    }
}
