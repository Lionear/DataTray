using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DataTray.Extensions.QueryBuilder.Tests.Golden;

public class SqlServerParseProofTests
{
    public static TheoryData<string, string> Cases() => FixtureCases.SqlCases("sqlserver");

    [Theory, MemberData(nameof(Cases))]
    public void Golden_ParsesWithoutErrors(string dialect, string name)
    {
        using var reader = new StringReader(FixtureCases.Sql(name, dialect));

        new TSql160Parser(true).Parse(reader, out var errors);

        Assert.Empty(errors.Select(e => $"{e.Line}:{e.Column} {e.Message}"));
    }
}
