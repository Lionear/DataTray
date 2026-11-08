using DataTray.Extensions.QueryBuilder.Model;
using static DataTray.Extensions.QueryBuilder.Tests.Generation.Build;

namespace DataTray.Extensions.QueryBuilder.Tests.Generation;

public class SubqueryGenerationTests
{
    private static readonly Binary Correlated = Bin(BinaryOp.Eq, Col("u", "Id"), Col("t", "Id"));

    private static Query Outer(Expr where) => Query(where, [Table("T", "t")], new SelectItem(Col("a"), null));

    private static Query Inner(Expr item, Expr? where) => Query(where, [Table("U", "u")], new SelectItem(item, null));

    public static TheoryData<string, Query, string> SubqueryCases() => new()
    {
        {
            "sqlserver",
            Outer(new Exists(Inner(Num("1"), Correlated), false)),
            "SELECT [a]\nFROM [dbo].[T] AS t\nWHERE EXISTS (\n  SELECT 1\n  FROM [dbo].[U] AS u\n  WHERE u.[Id] = t.[Id]\n);"
        },
        {
            "postgres",
            Outer(Bin(BinaryOp.And, new Exists(Inner(Num("1"), null), true), Col("b"))),
            "SELECT \"a\"\nFROM \"dbo\".\"T\" AS t\nWHERE NOT EXISTS (\n  SELECT 1\n  FROM \"dbo\".\"U\" AS u\n) AND \"b\";"
        },
        {
            "mysql",
            Outer(new InSubquery(Col("a"), Inner(Col("b"), null), true)),
            "SELECT `a`\nFROM `dbo`.`T` AS t\nWHERE `a` NOT IN (\n  SELECT `b`\n  FROM `dbo`.`U` AS u\n);"
        },
        {
            "postgres",
            Outer(new Quantified(Col("a"), BinaryOp.Gt, Quantifier.All, Inner(Col("b"), null))),
            "SELECT \"a\"\nFROM \"dbo\".\"T\" AS t\nWHERE \"a\" > ALL (\n  SELECT \"b\"\n  FROM \"dbo\".\"U\" AS u\n);"
        },
        {
            "sqlserver",
            Outer(new Quantified(Bin(BinaryOp.Add, Col("a"), Num("1")), BinaryOp.Eq, Quantifier.Any, Inner(Col("b"), null))),
            "SELECT [a]\nFROM [dbo].[T] AS t\nWHERE [a] + 1 = ANY (\n  SELECT [b]\n  FROM [dbo].[U] AS u\n);"
        },
        {
            "sqlite",
            Query(null, [Table("T", "t")], new SelectItem(Col("a"), null), new SelectItem(new ScalarSubquery(Inner(new FunctionCall("MAX", [Col("b")], false), Correlated)), "m")),
            "SELECT \"a\",\n       (\n         SELECT MAX(\"b\")\n         FROM \"dbo\".\"U\" AS u\n         WHERE u.\"Id\" = t.\"Id\"\n       ) AS \"m\"\nFROM \"dbo\".\"T\" AS t;"
        },
        {
            "sqlserver",
            Outer(new Exists(Inner(Num("1"), new Exists(Query(null, [Table("V", "v")], new SelectItem(Num("1"), null)), false)), false)),
            "SELECT [a]\nFROM [dbo].[T] AS t\nWHERE EXISTS (\n  SELECT 1\n  FROM [dbo].[U] AS u\n  WHERE EXISTS (\n    SELECT 1\n    FROM [dbo].[V] AS v\n  )\n);"
        },
        {
            "postgres",
            Query(Bin(BinaryOp.Gt, new ScalarSubquery(Query(Num("1"))), Num("0")), [], new SelectItem(Num("1"), null)),
            "SELECT 1\nWHERE (\n  SELECT 1\n) > 0;"
        },
    };

    [Theory, MemberData(nameof(SubqueryCases))]
    public void Generate_WritesNestedQueriesIndentedInParentheses(string provider, Query query, string expected)
    {
        Assert.Equal(expected, Generate(provider, null, query).Sql);
    }

    [Fact]
    public void Generate_QuantifiedSubqueryOnSqlite_IsAProblem()
    {
        var result = Generate("sqlite", "3.45.1", Outer(new Quantified(Col("a"), BinaryOp.Gt, Quantifier.All, Inner(Col("b"), null))));

        Assert.Null(result.Sql);
        Assert.Equal(["quantified-subquery"], Codes(result));
    }

    [Fact]
    public void Generate_DistinctSelect_WritesTheKeywordAndAlignsItems()
    {
        var query = new Query(null, new Select(true, [new SelectItem(Col("a"), null), new SelectItem(Col("b"), null)], [], null, [], null, []), [], null);

        Assert.Equal("SELECT DISTINCT \"a\",\n                \"b\";", Generate("postgres", null, query).Sql);
    }

    public static TheoryData<Query> LaterQueryParts() => new()
    {
        new Query(null, new Select(false, [new SelectItem(Col("a"), null)], [], null, [], null, []), [new OrderItem(Col("a"), false)], null),
        new Query(null, new Select(false, [new SelectItem(Col("a"), null)], [], null, [], null, []), [], new RowLimit(5, null)),
        new Query(new With(false, [new Cte("c", null, Query(Num("1")))]), new Select(false, [new SelectItem(Col("a"), null)], [], null, [], null, []), [], null),
        new Query(null, new Select(false, [new SelectItem(Col("a"), null)], [], null, [Col("a")], null, []), [], null),
        new Query(null, new Select(false, [new SelectItem(Col("a"), null)], [], null, [], Col("a"), []), [], null),
        new Query(null, new SetOperation(new Select(false, [new SelectItem(Col("a"), null)], [], null, [], null, []), SetOp.Union, false, new Select(false, [new SelectItem(Col("a"), null)], [], null, [], null, [])), [], null),
    };

    [Theory, MemberData(nameof(LaterQueryParts))]
    public void Generate_QueryPartNotYetWritten_IsAProblem(Query query)
    {
        var result = Generate("postgres", null, query);

        Assert.Null(result.Sql);
        Assert.Equal(["not-yet-supported"], Codes(result));
    }

    public static TheoryData<Statement> DmlStatements() => new()
    {
        new DeleteStatement(null, new TableName("dbo", "t"), null, [], null, null),
        new UpdateStatement(null, new TableName("dbo", "t"), null, [new Assignment("a", Num("1"))], [], null, null),
        new InsertStatement(null, new TableName("dbo", "t"), ["a"], new DefaultValuesSource(), null, null),
        new MergeStatement(new TableName("dbo", "t"), "t", Table("s", "s"), Col("a"), [new MergeClause(MergeMatch.Matched, null, new MergeDelete())], null),
    };

    [Theory, MemberData(nameof(DmlStatements))]
    public void Generate_DmlStatement_IsNotYetSupported(Statement statement)
    {
        var result = Generate("postgres", statement);

        Assert.Null(result.Sql);
        Assert.Equal(["dml-not-yet"], Codes(result));
    }
}
