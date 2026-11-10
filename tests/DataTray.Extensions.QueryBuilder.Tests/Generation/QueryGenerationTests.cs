using DataTray.Extensions.QueryBuilder.Model;
using static DataTray.Extensions.QueryBuilder.Tests.Generation.Build;

namespace DataTray.Extensions.QueryBuilder.Tests.Generation;

public class QueryGenerationTests
{
    private static readonly Select A = Sel("T", "a");
    private static readonly Select B = Sel("U", "b");
    private static readonly Select C = Sel("V", "c");
    private static readonly OrderItem ByA = new(Col("a"), false);
    private static readonly FunctionCall CountAll = new("COUNT", [new Star(null)], false);
    private static readonly FunctionCall SumX = new("SUM", [Col("x")], false);

    private static Select Sel(string table, string column) =>
        new(false, [new SelectItem(Col(column), null)], [Table(table, null)], null, [], null, []);

    private static Select Sel(Source from, Expr item) =>
        new(false, [new SelectItem(item, null)], [from], null, [], null, []);

    private static Query Q(QueryBody body, OrderItem[] orderBy, RowLimit? limit) => new(null, body, orderBy, limit);

    private static Query Q(QueryBody body) => new(null, body, [], null);

    private static SetOperation Set(QueryBody left, SetOp op, bool all, QueryBody right) => new(left, op, all, right);

    private static RowLimit Limit(long count, long? offset) => new(count, offset);

    private static Query FromSubquery(Query inner) =>
        Q(Sel(new SubquerySource(inner, "s", false), Col("s", "a")));

    private static Query WithCte(bool recursive, Query cteQuery, Query main) =>
        main with { With = new With(recursive, [new Cte("c", null, cteQuery)]) };

    private static Query RecursiveCount() => new(
        new With(true, [new Cte("c", ["n"], Q(Set(
            new Select(false, [new SelectItem(Num("1"), null)], [], null, [], null, []),
            SetOp.Union,
            true,
            new Select(false, [new SelectItem(Bin(BinaryOp.Add, Col("n"), Num("1")), null)], [new CteSource("c", null)], Bin(BinaryOp.Lt, Col("n"), Num("10")), [], null, []))))]),
        Sel(new CteSource("c", null), Col("n")),
        [],
        null);

    private static Select WindowSelect(string reference, params NamedWindow[] windows) =>
        new(false, [new SelectItem(new WindowCall(SumX, new WindowSpec(reference, [], [], null)), null)], [Table("T", null)], null, [], null, windows);

    private static NamedWindow Window(string name, string? baseWindow, Expr[] partitionBy, OrderItem[] orderBy) =>
        new(name, new WindowSpec(baseWindow, partitionBy, orderBy, null));

    private static ValuesBody Values(params Expr[][] rows) => new(rows.Select(r => new ValueList<Expr>(r)).ToArray());

    public static TheoryData<string, string?, Query, string> QueryCases() => new()
    {
        // SQL Server limits (Review Focus 4)
        {
            "sqlserver", null,
            Q(A, [ByA], Limit(5, null)),
            "SELECT TOP (5) [a]\nFROM [dbo].[T]\nORDER BY [a];"
        },
        {
            "sqlserver", null,
            Q(new Select(true, [new SelectItem(Col("a"), null), new SelectItem(Col("b"), null)], [Table("T", null)], null, [], null, []), [], Limit(10, null)),
            "SELECT DISTINCT TOP (10) [a],\n                         [b]\nFROM [dbo].[T];"
        },
        {
            "sqlserver", null,
            Q(A, [new OrderItem(Col("a"), true)], Limit(5, 10)),
            "SELECT [a]\nFROM [dbo].[T]\nORDER BY [a] DESC\nOFFSET 10 ROWS FETCH NEXT 5 ROWS ONLY;"
        },
        {
            "sqlserver", null,
            Q(Set(A, SetOp.Union, true, B), [ByA], Limit(5, null)),
            "SELECT [a]\nFROM [dbo].[T]\nUNION ALL\nSELECT [b]\nFROM [dbo].[U]\nORDER BY [a]\nOFFSET 0 ROWS FETCH NEXT 5 ROWS ONLY;"
        },
        {
            "sqlserver", null,
            FromSubquery(Q(A, [ByA], Limit(1, null))),
            "SELECT s.[a]\nFROM (\n  SELECT TOP (1) [a]\n  FROM [dbo].[T]\n  ORDER BY [a]\n) AS s;"
        },
        {
            "sqlserver", null,
            Q(Set(B, SetOp.Union, false, new NestedQuery(Q(A, [], Limit(1, null))))),
            "SELECT [b]\nFROM [dbo].[U]\nUNION\n(\n  SELECT TOP (1) [a]\n  FROM [dbo].[T]\n);"
        },
        {
            "sqlserver", null,
            Q(A, [], Limit(0, null)),
            "SELECT TOP (0) [a]\nFROM [dbo].[T];"
        },

        // A NestedQuery as the whole body of a subquery is written once inside the subquery's parentheses
        {
            "sqlserver", null,
            Where(new InSubquery(Col("a"), Q(new NestedQuery(Q(A))), false)),
            "SELECT 1\nWHERE [a] IN (\n  SELECT [a]\n  FROM [dbo].[T]\n);"
        },
        {
            "sqlserver", null,
            Where(new Exists(Q(new NestedQuery(Q(A))), false)),
            "SELECT 1\nWHERE EXISTS (\n  SELECT [a]\n  FROM [dbo].[T]\n);"
        },
        {
            "sqlserver", null,
            Query(new ScalarSubquery(Q(new NestedQuery(Q(A))))),
            "SELECT (\n  SELECT [a]\n  FROM [dbo].[T]\n);"
        },
        {
            "sqlserver", null,
            FromSubquery(Q(new NestedQuery(Q(A, [ByA], Limit(1, null))))),
            "SELECT s.[a]\nFROM (\n  SELECT TOP (1) [a]\n  FROM [dbo].[T]\n  ORDER BY [a]\n) AS s;"
        },
        {
            "postgres", null,
            Where(new InSubquery(Col("a"), Q(new NestedQuery(Q(A, [ByA], Limit(1, null)))), false)),
            "SELECT 1\nWHERE \"a\" IN (\n  SELECT \"a\"\n  FROM \"dbo\".\"T\"\n  ORDER BY \"a\"\n  LIMIT 1\n);"
        },
        {
            "postgres", null,
            Where(new Exists(Q(new NestedQuery(Q(A))), true)),
            "SELECT 1\nWHERE NOT EXISTS (\n  SELECT \"a\"\n  FROM \"dbo\".\"T\"\n);"
        },
        {
            "postgres", null,
            Query(new ScalarSubquery(Q(new NestedQuery(Q(A))))),
            "SELECT (\n  SELECT \"a\"\n  FROM \"dbo\".\"T\"\n);"
        },
        {
            "postgres", null,
            FromSubquery(Q(new NestedQuery(Q(A)))),
            "SELECT s.\"a\"\nFROM (\n  SELECT \"a\"\n  FROM \"dbo\".\"T\"\n) AS s;"
        },

        // LIMIT dialects
        {
            "postgres", null,
            Q(A, [new OrderItem(Col("a"), true), new OrderItem(Col("b"), false)], Limit(5, null)),
            "SELECT \"a\"\nFROM \"dbo\".\"T\"\nORDER BY \"a\" DESC, \"b\"\nLIMIT 5;"
        },
        {
            "mysql", null,
            Q(A, [], Limit(5, 10)),
            "SELECT `a`\nFROM `dbo`.`T`\nLIMIT 5 OFFSET 10;"
        },
        {
            "sqlite", null,
            Q(Set(A, SetOp.Except, false, B), [], Limit(3, null)),
            "SELECT \"a\"\nFROM \"dbo\".\"T\"\nEXCEPT\nSELECT \"b\"\nFROM \"dbo\".\"U\"\nLIMIT 3;"
        },
        {
            "postgres", null,
            FromSubquery(Q(A, [ByA], null)),
            "SELECT s.\"a\"\nFROM (\n  SELECT \"a\"\n  FROM \"dbo\".\"T\"\n  ORDER BY \"a\"\n) AS s;"
        },
        {
            "postgres", null,
            Where(new InSubquery(Col("a"), Q(B, [], Limit(1, null)), false)),
            "SELECT 1\nWHERE \"a\" IN (\n  SELECT \"b\"\n  FROM \"dbo\".\"U\"\n  LIMIT 1\n);"
        },

        // GROUP BY, HAVING, WINDOW
        {
            "sqlserver", null,
            Q(new Select(false, [new SelectItem(Col("a"), null), new SelectItem(CountAll, "n")], [Table("T", null)], null, [Col("a"), Col("b")], Bin(BinaryOp.And, Bin(BinaryOp.Gt, CountAll, Num("1")), new Literal(LiteralKind.Boolean, "true")), [])),
            "SELECT [a],\n       COUNT(*) AS [n]\nFROM [dbo].[T]\nGROUP BY [a], [b]\nHAVING COUNT(*) > 1 AND 1 = 1;"
        },
        {
            "postgres", null,
            Q(
                new Select(
                    false,
                    [new SelectItem(Col("a"), null), new SelectItem(new WindowCall(SumX, new WindowSpec("w", [], [], null)), "s")],
                    [Table("T", null)],
                    Bin(BinaryOp.Gt, Col("a"), Num("0")),
                    [Col("a"), Col("x")],
                    Bin(BinaryOp.Gt, CountAll, Num("1")),
                    [Window("w", null, [Col("a")], [])]),
                [new OrderItem(Col("a"), true)],
                Limit(10, 20)),
            "SELECT \"a\",\n       SUM(\"x\") OVER w AS \"s\"\nFROM \"dbo\".\"T\"\nWHERE \"a\" > 0\nGROUP BY \"a\", \"x\"\nHAVING COUNT(*) > 1\nWINDOW w AS (PARTITION BY \"a\")\nORDER BY \"a\" DESC\nLIMIT 10 OFFSET 20;"
        },
        {
            "postgres", null,
            Q(WindowSelect("Win", Window("w", null, [Col("a")], []), Window("Win", "w", [], [new OrderItem(Col("b"), false)]))),
            "SELECT SUM(\"x\") OVER \"Win\"\nFROM \"dbo\".\"T\"\nWINDOW w AS (PARTITION BY \"a\"), \"Win\" AS (w ORDER BY \"b\");"
        },
        {
            "sqlserver", "16.0.1000",
            Q(WindowSelect("w", Window("w", null, [], [ByA]))),
            "SELECT SUM([x]) OVER w\nFROM [dbo].[T]\nWINDOW w AS (ORDER BY [a]);"
        },

        // WITH
        {
            "postgres", null,
            RecursiveCount(),
            "WITH RECURSIVE \"c\" (\"n\") AS (\n  SELECT 1\n  UNION ALL\n  SELECT \"n\" + 1\n  FROM \"c\"\n  WHERE \"n\" < 10\n)\nSELECT \"n\"\nFROM \"c\";"
        },
        {
            "sqlite", null,
            RecursiveCount(),
            "WITH RECURSIVE \"c\" (\"n\") AS (\n  SELECT 1\n  UNION ALL\n  SELECT \"n\" + 1\n  FROM \"c\"\n  WHERE \"n\" < 10\n)\nSELECT \"n\"\nFROM \"c\";"
        },
        {
            "sqlserver", null,
            RecursiveCount(),
            "WITH [c] ([n]) AS (\n  SELECT 1\n  UNION ALL\n  SELECT [n] + 1\n  FROM [c]\n  WHERE [n] < 10\n)\nSELECT [n]\nFROM [c];"
        },
        {
            "mysql", null,
            new Query(new With(false, [new Cte("a", null, Query(Num("1"))), new Cte("b", null, Query(Num("2")))]), Sel(new CteSource("b", null), new Star(null)), [], null),
            "WITH `a` AS (\n  SELECT 1\n),\n`b` AS (\n  SELECT 2\n)\nSELECT *\nFROM `b`;"
        },
        {
            "postgres", null,
            FromSubquery(WithCte(false, Query(Num("1")), Q(Sel(new CteSource("c", null), Col("a"))))),
            "SELECT s.\"a\"\nFROM (\n  WITH \"c\" AS (\n    SELECT 1\n  )\n  SELECT \"a\"\n  FROM \"c\"\n) AS s;"
        },

        // Set operations
        {
            "postgres", null,
            Q(Set(Set(A, SetOp.Union, false, B), SetOp.Except, false, C)),
            "SELECT \"a\"\nFROM \"dbo\".\"T\"\nUNION\nSELECT \"b\"\nFROM \"dbo\".\"U\"\nEXCEPT\nSELECT \"c\"\nFROM \"dbo\".\"V\";"
        },
        {
            "postgres", null,
            Q(Set(A, SetOp.Union, false, Set(B, SetOp.Intersect, false, C))),
            "SELECT \"a\"\nFROM \"dbo\".\"T\"\nUNION\n(\n  SELECT \"b\"\n  FROM \"dbo\".\"U\"\n  INTERSECT\n  SELECT \"c\"\n  FROM \"dbo\".\"V\"\n);"
        },
        {
            "sqlserver", null,
            Q(Set(Set(A, SetOp.Union, false, B), SetOp.Intersect, false, C)),
            "(\n  SELECT [a]\n  FROM [dbo].[T]\n  UNION\n  SELECT [b]\n  FROM [dbo].[U]\n)\nINTERSECT\nSELECT [c]\nFROM [dbo].[V];"
        },
        {
            "sqlite", null,
            Q(Set(Set(A, SetOp.Union, false, B), SetOp.Intersect, false, C)),
            "SELECT \"a\"\nFROM \"dbo\".\"T\"\nUNION\nSELECT \"b\"\nFROM \"dbo\".\"U\"\nINTERSECT\nSELECT \"c\"\nFROM \"dbo\".\"V\";"
        },
        {
            "postgres", null,
            Q(Set(Set(A, SetOp.Intersect, false, B), SetOp.Union, false, C)),
            "SELECT \"a\"\nFROM \"dbo\".\"T\"\nINTERSECT\nSELECT \"b\"\nFROM \"dbo\".\"U\"\nUNION\nSELECT \"c\"\nFROM \"dbo\".\"V\";"
        },
        {
            "mysql", "8.0.31",
            Q(Set(A, SetOp.Intersect, true, B)),
            "SELECT `a`\nFROM `dbo`.`T`\nINTERSECT ALL\nSELECT `b`\nFROM `dbo`.`U`;"
        },
        {
            "mysql", "8.0.31",
            Q(Set(new NestedQuery(Q(A, [ByA], Limit(1, null))), SetOp.Union, false, B)),
            "(\n  SELECT `a`\n  FROM `dbo`.`T`\n  ORDER BY `a`\n  LIMIT 1\n)\nUNION\nSELECT `b`\nFROM `dbo`.`U`;"
        },

        // VALUES
        {
            "postgres", null,
            Q(Values([Num("1"), Str("x")], [Num("2"), Str("y")])),
            "VALUES (1, 'x'),\n       (2, 'y');"
        },
        {
            "mysql", "8.0.19",
            Q(Values([Num("1"), Str("x")], [Num("2"), Str("y")])),
            "VALUES ROW(1, 'x'),\n       ROW(2, 'y');"
        },
        {
            "sqlite", null,
            Q(Set(new Select(false, [new SelectItem(Num("1"), null)], [], null, [], null, []), SetOp.Union, false, Values([Num("2")]))),
            "SELECT 1\nUNION\nVALUES (2);"
        },
    };

    [Theory, MemberData(nameof(QueryCases))]
    public void Generate_WritesQueryClauses(string provider, string? version, Query query, string expected)
    {
        var result = Generate(provider, version, query);

        Assert.Empty(result.Problems);
        Assert.Equal(expected, result.Sql);
    }

    public static TheoryData<string, string?, Query, string> ProblemCases() => new()
    {
        { "sqlserver", null, FromSubquery(Q(A, [ByA], null)), "order-by-needs-limit" },
        { "sqlserver", null, WithCte(false, Q(A, [ByA], null), Q(Sel(new CteSource("c", null), Col("a")))), "order-by-needs-limit" },
        { "sqlserver", null, Where(new Exists(Q(A, [ByA], null), false)), "order-by-needs-limit" },
        { "sqlserver", null, Q(new NestedQuery(Q(A, [ByA], null))), "order-by-needs-limit" },
        { "sqlserver", null, Q(Set(B, SetOp.Union, false, new NestedQuery(Q(A, [ByA], Limit(1, null))))), "order-by-in-set-operand" },
        { "sqlserver", null, Q(Set(new NestedQuery(Q(A, [ByA], Limit(1, 5))), SetOp.Union, false, B)), "order-by-in-set-operand" },
        { "sqlserver", null, Q(Set(B, SetOp.Union, false, new NestedQuery(Q(new NestedQuery(Q(A, [ByA], Limit(5, null))))))), "order-by-in-set-operand" },
        { "sqlserver", null, Where(new InSubquery(Col("a"), Q(new NestedQuery(Q(A)), [ByA], Limit(1, null)), false)), "nested-query-body" },
        { "postgres", null, FromSubquery(Q(new NestedQuery(Q(A)), [], Limit(1, null))), "nested-query-body" },
        { "sqlserver", null, Q(A, [ByA], Limit(0, 5)), "invalid-limit" },
        { "postgres", null, new Query(new With(false, []), A, [], null), "empty-with" },
        { "postgres", null, new Query(new With(false, [new Cte("c", [], Query(Num("1")))]), A, [], null), "empty-cte-columns" },
        { "sqlserver", "15.0.2000", Q(WindowSelect("w", Window("w", null, [Col("a")], []), Window("v", null, [], [ByA]))), "named-window" },
        { "sqlserver", null, Q(A, [], Limit(5, 10)), "offset-needs-order-by" },
        { "sqlserver", null, Q(Set(A, SetOp.Union, false, B), [], Limit(5, null)), "offset-needs-order-by" },
        { "sqlserver", null, FromSubquery(WithCte(false, Query(Num("1")), Q(Sel(new CteSource("c", null), Col("a"))))), "nested-with" },
        { "sqlserver", null, Q(Set(A, SetOp.Intersect, true, B)), "intersect-except-all" },
        { "sqlite", null, Q(Set(A, SetOp.Except, true, B)), "intersect-except-all" },
        { "mysql", "8.0.30", Q(Set(A, SetOp.Intersect, false, B)), "intersect-except" },
        { "mysql", "8.0.30", Q(Set(A, SetOp.Except, true, B)), "intersect-except" },
        { "mysql", null, Q(Set(A, SetOp.Except, false, B)), "intersect-except" },
        { "mysql", "8.4.0", Where(new InSubquery(Col("a"), Q(B, [], Limit(1, null)), false)), "limit-in-in-subquery" },
        { "mysql", "8.4.0", Where(new Quantified(Col("a"), BinaryOp.Gt, Quantifier.All, Q(B, [], Limit(1, null)))), "limit-in-in-subquery" },
        { "sqlserver", "15.0.2000", Q(new Select(false, [new SelectItem(Col("a"), null)], [Table("T", null)], null, [], null, [Window("w", null, [Col("a")], [])])), "named-window" },
        { "sqlite", "3.24.0", Q(new Select(false, [new SelectItem(Col("a"), null)], [Table("T", null)], null, [], null, [Window("w", null, [Col("a")], [])])), "named-window" },
        { "sqlite", null, Q(Set(A, SetOp.Union, false, Set(B, SetOp.Union, false, C))), "parenthesized-query" },
        { "sqlite", null, Q(Set(new NestedQuery(Q(A, [ByA], Limit(1, null))), SetOp.Union, false, B)), "parenthesized-query" },
        { "sqlserver", null, Q(Values([Num("1")])), "values" },
        { "mysql", "8.0.18", Q(Values([Num("1")])), "values" },
        { "mysql", "8.4.0", Where(new InSubquery(Col("a"), Q(Set(new NestedQuery(Q(A, [], Limit(1, null))), SetOp.Union, false, B)), false)), "limit-in-in-subquery" },
        { "mysql", "8.4.0", Where(new Quantified(Col("a"), BinaryOp.Eq, Quantifier.Any, Q(new NestedQuery(Q(B, [], Limit(1, null)))))), "limit-in-in-subquery" },
        { "mysql", "8.0.30", Q(Set(A, SetOp.Union, false, Set(B, SetOp.Union, false, C))), "parenthesized-query" },
        { "mysql", null, Q(Set(new NestedQuery(Q(A, [ByA], Limit(1, null))), SetOp.Union, false, B)), "parenthesized-query" },
        { "postgres", null, Q(new Select(false, [], [Table("T", null)], null, [], null, [])), "empty-select" },
        { "postgres", null, Q(Values()), "empty-values" },
        { "sqlite", null, Q(Values([Num("1")], [])), "empty-values" },
        { "postgres", null, Q(A, [], Limit(-1, null)), "invalid-limit" },
        { "postgres", null, Q(A, [], Limit(5, -1)), "invalid-limit" },
    };

    [Theory, MemberData(nameof(ProblemCases))]
    public void Generate_QueryTheDialectCannotExpress_IsAProblem(string provider, string? version, Query query, string code)
    {
        var result = Generate(provider, version, query);

        Assert.Null(result.Sql);
        Assert.Equal([code], Codes(result));
    }
}
