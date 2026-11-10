using DataTray.Extensions.QueryBuilder.Dialects;
using DataTray.Extensions.QueryBuilder.Generation;
using DataTray.Extensions.QueryBuilder.Model;
using DataTray.Extensions.QueryBuilder.Reading;

namespace DataTray.Extensions.QueryBuilder.Tests.Golden;

public class RoundTripTests
{
    // Refused: SqlParserCS 0.6.5 can't parse EXCLUDE in a window frame; fragment-in-where's redundant parentheses
    // `((1 + 2))` read as 1 + 2, which the generator writes without them.
    private static readonly string[] RefusedGaps =
    [
        "window-frame-exclude-current-row", "window-frame-exclude-group", "window-frame-exclude-no-others", "window-frame-exclude-ties",
        "fragment-in-where",
    ];

    // The reader understands the fragment's text (a derived table); the model differs, the tokens don't.
    private static readonly string[] OtherModelGaps = ["fragment-source"];

    // Two models that print the same SQL: Negate(5) and -5, a NestedQuery and a bare set operand; '…' is only narrow in
    // T-SQL; SQL Server has no booleans, so `[Active] = 1` is the number 1.
    private static readonly string[] SameSqlGaps = ["where-negate", "set-right-nested", "literal-narrow-string"];

    private static readonly string[] SqlServerSameSqlGaps = ["where-boolean-literal", "where-boolean-false", "where-negate", "set-right-nested"];

    public static TheoryData<string, string> Cases() =>
        FixtureCases.SqlCases(n => !RefusedGaps.Contains(n) && !OtherModelGaps.Contains(n) && !SameSqlGaps.Contains(n), "postgres", "mysql", "sqlite");

    public static TheoryData<string, string> SqlServerCases() =>
        FixtureCases.SqlCases(n => !RefusedGaps.Contains(n) && !OtherModelGaps.Contains(n) && !SqlServerSameSqlGaps.Contains(n), "sqlserver");

    public static TheoryData<string, string> SameSqlCases() => FixtureCases.SqlCases(n => SameSqlGaps.Contains(n), "postgres", "mysql", "sqlite");

    public static TheoryData<string, string> SqlServerSameSqlCases() => FixtureCases.SqlCases(n => SqlServerSameSqlGaps.Contains(n), "sqlserver");

    public static TheoryData<string, string> OtherModelCases() => FixtureCases.SqlCases(n => OtherModelGaps.Contains(n), FixtureCases.Dialects);

    public static TheoryData<string, string> RefusedCases() => FixtureCases.SqlCases(n => RefusedGaps.Contains(n), FixtureCases.Dialects);

    [Theory, MemberData(nameof(Cases)), MemberData(nameof(SqlServerCases))]
    public void RoundTrip_EveryGolden_ReadsBackToItsModel(string dialect, string name)
    {
        var expected = QueryModelJson.Serialize(FixtureCases.Model(name));

        var result = ReadBack.ReadAndVerify(FixtureCases.Sql(name, dialect), dialect, FixtureCases.Version(dialect));

        Assert.Empty(result.Reasons);
        Assert.Equal(expected, QueryModelJson.Serialize(Assert.IsAssignableFrom<Statement>(result.Statement)));
    }

    [Theory, MemberData(nameof(SameSqlCases)), MemberData(nameof(SqlServerSameSqlCases))]
    public void RoundTrip_GoldenOfAModelThatPrintsLikeAnother_RegeneratesTheGolden(string dialect, string name)
    {
        var golden = FixtureCases.Sql(name, dialect);

        var result = ReadBack.ReadAndVerify(golden, dialect, FixtureCases.Version(dialect));

        var statement = Assert.IsAssignableFrom<Statement>(result.Statement);
        Assert.Equal(golden, SqlGenerator.Generate(statement, SqlDialects.For(dialect, FixtureCases.Version(dialect))).Sql);
    }

    [Theory, MemberData(nameof(OtherModelCases))]
    public void RoundTrip_GoldenWithAFragmentTheReaderUnderstands_Verifies(string dialect, string name)
    {
        var result = ReadBack.ReadAndVerify(FixtureCases.Sql(name, dialect), dialect, FixtureCases.Version(dialect));

        Assert.Empty(result.Reasons);
        Assert.Equal(ReadOutcome.Read, result.Outcome);
    }

    [Theory, MemberData(nameof(RefusedCases))]
    public void RoundTrip_GoldenTheReaderCannotKeep_CannotShow(string dialect, string name)
    {
        var result = ReadBack.ReadAndVerify(FixtureCases.Sql(name, dialect), dialect, FixtureCases.Version(dialect));

        Assert.Equal(ReadOutcome.CannotShow, result.Outcome);
        Assert.Null(result.Statement);
    }
}
