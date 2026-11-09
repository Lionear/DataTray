using DataTray.Extensions.QueryBuilder.Dialects;
using DataTray.Extensions.QueryBuilder.Generation;

namespace DataTray.Extensions.QueryBuilder.Tests.Golden;

public class GoldenTests
{
    public static TheoryData<string, string> Cases() => FixtureCases.AllCases();

    [Fact]
    public void Fixtures_AreAtLeastOneHundredCases()
    {
        Assert.True(FixtureCases.CaseNames().Length >= 100);
    }

    [Theory, MemberData(nameof(Cases))]
    public void Generate_MatchesTheGolden(string dialect, string name)
    {
        var expected = FixtureCases.Expected(name, dialect);

        var result = SqlGenerator.Generate(FixtureCases.Model(name), SqlDialects.For(dialect, FixtureCases.Version(dialect)));

        Assert.Equal(expected.Sql, result.Sql);
        Assert.Equal(expected.ProblemCodes, result.Problems.Select(p => p.Code).ToArray());
    }
}
