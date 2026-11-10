using DataTray.Extensions.QueryBuilder.Dialects;
using DataTray.Extensions.QueryBuilder.Reading;

namespace DataTray.Extensions.QueryBuilder.Tests.Reading;

public class SqlTokensTests
{
    public static TheoryData<string, string, string[]> AsCases() => new()
    {
        { "sqlserver", "SELECT a AS b, (SELECT 1) AS c FROM t AS x", ["SELECT", "A", "B", ",", "(", "SELECT", "1", ")", "C", "FROM", "T", "X"] },
        { "sqlserver", "SELECT TRY_CAST(a AS money) AS b", ["SELECT", "TRY_CAST", "(", "A", "AS", "MONEY", ")", "B"] },
        { "postgres", "SELECT CAST(a AS text)", ["select", "cast", "(", "a", "as", "text", ")"] },
        { "sqlserver", "SELECT * AS x FROM t", ["SELECT", "*", "AS", "X", "FROM", "T"] },
        { "mysql", "SELECT a AS 'b'", ["SELECT", "A", "AS", "'b'"] },
        { "postgres", "WITH x AS (SELECT 1) SELECT CASE WHEN a THEN 1 END AS b FROM x", ["with", "x", "as", "(", "select", "1", ")", "select", "case", "when", "a", "then", "1", "end", "b", "from", "x"] },
        { "mysql", "SELECT CONVERT(a, char) AS b FROM t WINDOW w AS (ORDER BY a)", ["SELECT", "CONVERT", "(", "A", ",", "CHAR", ")", "B", "FROM", "T", "WINDOW", "W", "AS", "(", "ORDER", "BY", "A", ")"] },
    };

    [Theory, MemberData(nameof(AsCases))]
    public void Normalize_As_DroppedOnlyBeforeAnAlias(string provider, string sql, string[] expected)
    {
        var tokens = SqlTokens.Normalize(sql, SqlDialects.For(provider, null));

        Assert.Equal(expected, tokens);
    }
}
