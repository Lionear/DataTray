using DataTray.Extensions.QueryBuilder.Model;
using DataTray.Extensions.QueryBuilder.Reading;

namespace DataTray.Extensions.QueryBuilder.Tests.Golden;

public class RoundTripTests
{
    // EXCLUDE: SqlParserCS 0.6.5 can't parse it. fragment-*: the fixture keeps as text what the reader understands.
    // where-negate / set-right-nested: Negate(5) and -5, a NestedQuery and a bare set operand print the same; the reader
    // reads the signed literal and the bare operand.
    private static readonly string[] KnownGaps =
    [
        "window-frame-exclude-current-row", "window-frame-exclude-group", "window-frame-exclude-no-others", "window-frame-exclude-ties",
        "fragment-source", "fragment-in-where", "where-negate", "set-right-nested",
    ];

    public static TheoryData<string, string> Cases() => FixtureCases.SqlCases(n => !KnownGaps.Contains(n), "postgres", "mysql", "sqlite");

    [Theory, MemberData(nameof(Cases))]
    public void RoundTrip_Golden_ReadsBackToItsModel(string dialect, string name)
    {
        var expected = QueryModelJson.Serialize(FixtureCases.Model(name));

        var result = SqlReaders.For(dialect).Read(FixtureCases.Sql(name, dialect));

        Assert.Empty(result.Reasons);
        Assert.Equal(expected, QueryModelJson.Serialize(Assert.IsAssignableFrom<Statement>(result.Statement)));
    }
}
