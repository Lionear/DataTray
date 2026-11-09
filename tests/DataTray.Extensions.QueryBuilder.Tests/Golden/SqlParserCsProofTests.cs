using SqlParser;
using SqlParser.Dialects;

namespace DataTray.Extensions.QueryBuilder.Tests.Golden;

public class SqlParserCsProofTests
{
    // SqlParserCS 0.6.5 lacks the PostgreSQL frame EXCLUDE clause; SqliteProofTests proves that syntax.
    private static readonly string[] ParserGaps = ["window-frame-exclude-current-row", "window-frame-exclude-no-others", "window-frame-exclude-ties"];

    public static TheoryData<string, string> Cases() => FixtureCases.SqlCases(n => !ParserGaps.Contains(n), "postgres", "mysql");

    private static Dialect For(string dialect) => dialect == "postgres" ? new PostgreSqlDialect() : new MySqlDialect();

    [Theory, MemberData(nameof(Cases))]
    public void Golden_ParsesWithoutErrors(string dialect, string name)
    {
        var sql = FixtureCases.Sql(name, dialect);

        var exception = Record.Exception(() => new Parser().ParseSql(sql, For(dialect)));

        Assert.Null(exception);
    }
}
