using DataTray.Extensions.QueryBuilder.Model;
using static DataTray.Extensions.QueryBuilder.Tests.Generation.Build;

namespace DataTray.Extensions.QueryBuilder.Tests.Generation;

public class SourceGenerationTests
{
    private static readonly SelectItem All = new(new Star(null), null);

    private static JoinSource Join(Source left, JoinType type, Source right, Expr? on) => new(left, type, right, on);

    private static Binary Eq(Expr left, Expr right) => Bin(BinaryOp.Eq, left, right);

    private static Query Inner(string table, string alias, Expr? where) =>
        Query(where, [Table(table, alias)], new SelectItem(Col("n"), null));

    public static TheoryData<string, string?, Query, string> SourceCases() => new()
    {
        {
            "sqlserver", null,
            Query(null, [Join(Join(Table("Researcher", "r"), JoinType.Inner, Table("Paper", "p"), Eq(Col("p", "AuthorId"), Col("r", "Id"))), JoinType.Left, Table("Institution", "i"), Eq(Col("i", "Id"), Col("r", "InstitutionId")))], new SelectItem(Col("r", "FullName"), null), new SelectItem(Col("p", "Title"), null)),
            "SELECT r.[FullName],\n       p.[Title]\nFROM [dbo].[Researcher] AS r\nINNER JOIN [dbo].[Paper] AS p ON p.[AuthorId] = r.[Id]\nLEFT JOIN [dbo].[Institution] AS i ON i.[Id] = r.[InstitutionId];"
        },
        {
            "postgres", null,
            Query(null, [Join(Table("A", "a"), JoinType.Full, Table("B", "b"), Eq(Col("a", "Id"), Col("b", "Id")))], All),
            "SELECT *\nFROM \"dbo\".\"A\" AS a\nFULL JOIN \"dbo\".\"B\" AS b ON a.\"Id\" = b.\"Id\";"
        },
        {
            "sqlite", "3.39.0",
            Query(null, [Join(Table("A", "a"), JoinType.Right, Table("B", "b"), Eq(Col("a", "Id"), Col("b", "Id")))], All),
            "SELECT *\nFROM \"dbo\".\"A\" AS a\nRIGHT JOIN \"dbo\".\"B\" AS b ON a.\"Id\" = b.\"Id\";"
        },
        {
            "mysql", null,
            Query(null, [Join(Table("A", "a"), JoinType.Cross, Table("B", "b"), null)], All),
            "SELECT *\nFROM `dbo`.`A` AS a\nCROSS JOIN `dbo`.`B` AS b;"
        },
        {
            "sqlserver", null,
            Query(null, [Join(Table("A", "a"), JoinType.Inner, Join(Table("B", "b"), JoinType.Inner, Table("C", "c"), Eq(Col("b", "Id"), Col("c", "Id"))), Eq(Col("a", "Id"), Col("b", "Id")))], All),
            "SELECT *\nFROM [dbo].[A] AS a\nINNER JOIN ([dbo].[B] AS b INNER JOIN [dbo].[C] AS c ON b.[Id] = c.[Id]) ON a.[Id] = b.[Id];"
        },
        {
            "sqlite", null,
            Query(null, [Table("A", "a"), new TableSource(new TableName(null, "B"), null)], All),
            "SELECT *\nFROM \"dbo\".\"A\" AS a,\n     \"B\";"
        },
        {
            "sqlserver", null,
            Query(null, [Table("A", "my alias")], All),
            "SELECT *\nFROM [dbo].[A] AS [my alias];"
        },
        {
            "postgres", null,
            Query(null, [new CteSource("Tree", "t"), new CteSource("Le\"af", null)], All),
            "SELECT *\nFROM \"Tree\" AS t,\n     \"Le\"\"af\";"
        },
        {
            "postgres", null,
            Query(null, [new FragmentSource("generate_series(1, 3) AS g(n)")], All),
            "SELECT *\nFROM generate_series(1, 3) AS g(n);"
        },
        {
            "mysql", null,
            Query(null, [new SubquerySource(Inner("T", "x", null), "t", false)], new SelectItem(Col("t", "n"), null)),
            "SELECT t.`n`\nFROM (\n  SELECT `n`\n  FROM `dbo`.`T` AS x\n) AS t;"
        },
        {
            "postgres", null,
            Query(null, [Join(Table("A", "a"), JoinType.Cross, new SubquerySource(Inner("B", "b", Eq(Col("b", "Id"), Col("a", "Id"))), "l", true), null)], All),
            "SELECT *\nFROM \"dbo\".\"A\" AS a\nCROSS JOIN LATERAL (\n  SELECT \"n\"\n  FROM \"dbo\".\"B\" AS b\n  WHERE b.\"Id\" = a.\"Id\"\n) AS l;"
        },
        {
            "sqlserver", null,
            Query(null, [Join(Table("A", "a"), JoinType.OuterApply, new SubquerySource(Inner("B", "b", Eq(Col("b", "Id"), Col("a", "Id"))), "l", false), null)], All),
            "SELECT *\nFROM [dbo].[A] AS a\nOUTER APPLY (\n  SELECT [n]\n  FROM [dbo].[B] AS b\n  WHERE b.[Id] = a.[Id]\n) AS l;"
        },
    };

    [Theory, MemberData(nameof(SourceCases))]
    public void Generate_WritesSources(string provider, string? version, Query query, string expected)
    {
        Assert.Equal(expected, Generate(provider, version, query).Sql);
    }

    public static TheoryData<string, string?, Query, string[]> MissingCapabilities() => new()
    {
        { "sqlite", "3.38.0", Query(null, [Join(Table("A", "a"), JoinType.Right, Table("B", "b"), null)], All), ["right-join"] },
        { "sqlite", null, Query(null, [Join(Table("A", "a"), JoinType.Full, Table("B", "b"), null)], All), ["full-join"] },
        { "mysql", null, Query(null, [Join(Table("A", "a"), JoinType.Full, Table("B", "b"), null)], All), ["full-join"] },
        { "sqlserver", null, Query(null, [Join(Table("A", "a"), JoinType.Cross, new SubquerySource(Inner("B", "b", null), "l", true), null)], All), ["lateral"] },
        { "sqlite", null, Query(null, [Join(Table("A", "a"), JoinType.Cross, new SubquerySource(Inner("B", "b", null), "l", true), null)], All), ["lateral"] },
        { "mysql", "8.0.13", Query(null, [Join(Table("A", "a"), JoinType.Cross, new SubquerySource(Inner("B", "b", null), "l", true), null)], All), ["lateral"] },
        { "postgres", null, Query(null, [Join(Table("A", "a"), JoinType.CrossApply, new SubquerySource(Inner("B", "b", null), "l", false), null)], All), ["apply"] },
        { "mysql", "8.0.14", Query(null, [Join(Table("A", "a"), JoinType.OuterApply, new SubquerySource(Inner("B", "b", null), "l", true), null)], All), ["apply"] },
        { "sqlite", null, Query(null, [Join(Table("A", "a"), JoinType.CrossApply, new SubquerySource(Inner("B", "b", null), "l", false), null)], All), ["apply"] },
        {
            "sqlite", null,
            Query(null, [Join(Join(Table("A", "a"), JoinType.Right, Table("B", "b"), null), JoinType.Full, Table("C", "c"), null)], new SelectItem(new WindowCall(new FunctionCall("RANK", [], false), new WindowSpec(null, [], [], null)), null)),
            ["window-functions", "right-join", "full-join"]
        },
    };

    [Theory, MemberData(nameof(MissingCapabilities))]
    public void Generate_MissingSourceCapability_ReportsEveryProblemAndNoSql(string provider, string? version, Query query, string[] codes)
    {
        var result = Generate(provider, version, query);

        Assert.Null(result.Sql);
        Assert.Equal(codes, Codes(result));
    }

    [Fact]
    public void Generate_LateralWhereSupported_WritesTheKeyword()
    {
        var query = Query(null, [Join(Table("A", "a"), JoinType.Cross, new SubquerySource(Inner("B", "b", null), "l", true), null)], All);

        Assert.Equal("SELECT *\nFROM `dbo`.`A` AS a\nCROSS JOIN LATERAL (\n  SELECT `n`\n  FROM `dbo`.`B` AS b\n) AS l;", Generate("mysql", "8.0.14", query).Sql);
    }
}
