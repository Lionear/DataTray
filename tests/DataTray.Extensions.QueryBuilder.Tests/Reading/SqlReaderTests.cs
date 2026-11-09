using DataTray.Extensions.QueryBuilder.Model;
using DataTray.Extensions.QueryBuilder.Reading;
using static DataTray.Extensions.QueryBuilder.Tests.Generation.Build;

namespace DataTray.Extensions.QueryBuilder.Tests.Reading;

public class SqlReaderTests
{
    public static TheoryData<string, string, (string, int)[]> UnshowableCases() => new()
    {
        { "postgres", "SELECT 1;\nSELECT 2;", [("multiple-statements", 2)] },
        { "postgres", "BEGIN;\nSELECT 1;\nCOMMIT;", [("transaction", 1), ("multiple-statements", 2), ("transaction", 3)] },
        { "mysql", "SET @x = 1;\n\nSELECT @x;", [("set-variable", 1), ("variable", 1), ("multiple-statements", 3), ("variable", 3)] },
        { "mysql", "DECLARE x INT;", [("declare", 1)] },
        { "mysql", "# setup\nCALL refresh();", [("procedure-call", 2)] },
        { "mysql", "START TRANSACTION;", [("transaction", 1)] },
        { "postgres", "/* a;\n b */ CREATE TABLE t (a int);", [("ddl", 2)] },
        { "sqlite", "SELECT 1;\n-- x\nDROP TABLE t;", [("multiple-statements", 3), ("ddl", 3)] },
        { "postgres", "TRUNCATE t;", [("ddl", 1)] },
        { "postgres", "EXECUTE plan1;", [("procedure-call", 1)] },
        { "mysql", "IF 1 = 1 THEN\n  DO 0;\nEND IF", [("control-flow", 1), ("multiple-statements", 3)] },
        { "postgres", "INSERT INTO t VALUES (1);", [("dml-not-yet", 1)] },
        { "mysql", "\nUPDATE t SET a = 1;", [("dml-not-yet", 2)] },
        { "sqlite", "DELETE FROM t;", [("dml-not-yet", 1)] },
        { "postgres", "MERGE INTO t USING s ON t.id = s.id WHEN MATCHED THEN DELETE;", [("dml-not-yet", 1)] },
        { "mysql", "SHOW TABLES;", [("not-a-query", 1)] },
        { "postgres", "  ", [("empty", 1)] },
        { "postgres", "\nSELECT DISTINCT ON (a) a FROM t;", [("cannot-represent", 2)] },
        { "postgres", "SELECT 1\nWHERE a = E'x\\n';", [("escape-string", 2)] },
        { "mysql", "SELECT /*! 1 + */ 2;", [("mysql-executable-comment", 1)] },
        { "mysql", "SELECT\n/*+ NO_ICP(t) */ a FROM t;", [("mysql-executable-comment", 2)] },
        { "mysql", "SELECT 1 --1\n;", [("mysql-double-dash", 1)] },
        { "mysql", "SELECT 1 WHERE a LIKE '50\\%';", [("mysql-like-escape", 1)] },
        { "postgres", "SELECT 'a'\n'b';", [("adjacent-strings", 2)] },
        { "sqlite", "SELECT 'a' /* x */ 'b';", [("adjacent-strings", 1)] },
        { "postgres", "(SELECT a FROM t FETCH FIRST 1 ROWS ONLY)\nUNION\nSELECT a FROM u;", [("cannot-represent", 1)] },
        { "postgres", "SELECT a FROM u\nUNION\n(SELECT a FROM t FOR UPDATE);", [("cannot-represent", 1)] },
        { "postgres", "SELECT x.a FROM ((SELECT a FROM t FETCH FIRST 1 ROWS ONLY)) AS x;", [("cannot-represent", 1)] },
        { "mysql", "SELECT a || b FROM t;", [("mysql-operator", 1)] },
        { "mysql", "SELECT a XOR b FROM t;", [("mysql-operator", 1)] },
        { "postgres", "SELECT * FROM ONLY t;", [("only", 1)] },
        { "postgres", "SELECT * FROM a\nJOIN ONLY b ON TRUE;", [("only", 2)] },
        { "postgres", "SELECT U&'d\\0061t';", [("prefixed-string", 1)] },
        { "postgres", "SELECT B'101';", [("prefixed-string", 1)] },
        { "mysql", "SELECT b'1';", [("prefixed-string", 1)] },
        { "mysql", "SELECT X'4D';", [("prefixed-string", 1)] },
        { "mysql", "SELECT _utf8mb4'a';", [("prefixed-string", 1)] },
        { "mysql", "SELECT DISTINCTROW a FROM t;", [("mysql-modifier", 1)] },
        { "mysql", "SELECT SQL_CALC_FOUND_ROWS a FROM t;", [("mysql-modifier", 1)] },
        { "mysql", "SELECT @@version;", [("variable", 1)] },
        { "postgres", "SELECT 1 WHERE a IN (VALUES (1), (2));", [("cannot-represent", 1)] },
        { "sqlite", "SELECT a::int FROM t;", [("sqlite-cast", 1)] },
        { "sqlserver", "DECLARE @x INT;\nSELECT @x;", [("declare", 1), ("variable", 1), ("multiple-statements", 2), ("variable", 2)] },
        { "sqlserver", "SET NOCOUNT ON;", [("set-variable", 1)] },
        { "sqlserver", "SELECT 1\nGO\nSELECT 2", [("multiple-statements", 3)] },
        { "sqlserver", "SELECT 1 SELECT 2", [("multiple-statements", 1)] },
        { "sqlserver", "IF 1 = 1\n  SELECT 1;", [("control-flow", 1)] },
        { "sqlserver", "WHILE 1 = 1 BREAK;", [("control-flow", 1)] },
        { "sqlserver", "BEGIN TRY\n  SELECT 1;\nEND TRY\nBEGIN CATCH\nEND CATCH", [("control-flow", 1), ("multiple-statements", 3)] },
        { "sqlserver", "BEGIN TRAN;\nSELECT 1;\nCOMMIT;", [("transaction", 1), ("multiple-statements", 2), ("transaction", 3)] },
        { "sqlserver", "BEGIN DISTRIBUTED TRANSACTION;", [("transaction", 1)] },
        { "sqlserver", "EXEC sp_who;", [("procedure-call", 1)] },
        { "sqlserver", "/* a /* b */ ; */ CREATE TABLE t (a int);", [("ddl", 1)] },
        { "sqlserver", "SELECT TOP (@n) a FROM t;", [("variable", 1)] },
        { "sqlserver", "SELECT @v = a FROM t;", [("variable", 1)] },
        { "sqlserver", "SELECT @@ROWCOUNT;", [("variable", 1)] },
        { "sqlserver", "SELECT TOP 10 PERCENT a FROM t;", [("cannot-represent", 1)] },
        { "sqlserver", "SELECT TOP (5) WITH TIES a FROM t ORDER BY a;", [("cannot-represent", 1)] },
        { "sqlserver", "SELECT a FROM t\nFOR JSON PATH;", [("cannot-represent", 2)] },
        { "sqlserver", "SELECT a FROM t\nFOR XML AUTO;", [("cannot-represent", 2)] },
        { "sqlserver", "SELECT a\nINTO t2\nFROM t;", [("cannot-represent", 2)] },
        { "sqlserver", "SELECT a FROM t\nOPTION (RECOMPILE);", [("cannot-represent", 2)] },
        { "sqlserver", "SELECT a FROM t\nORDER BY a OFFSET 5 ROWS;", [("cannot-represent", 2)] },
        { "sqlserver", "SELECT a FROM t\nGROUP BY a WITH ROLLUP;", [("cannot-represent", 2)] },
        { "sqlserver", "INSERT INTO t VALUES (1);", [("dml-not-yet", 1)] },
        { "sqlserver", ";WITH x AS (SELECT 1 AS a)\nDELETE FROM t;", [("dml-not-yet", 1)] },
        { "sqlserver", "PRINT 1;", [("not-a-query", 1)] },
        { "sqlserver", "SELECT 1\nFROM t WHERE;", [("parse-error", 2)] },
    };

    [Theory, MemberData(nameof(UnshowableCases))]
    public void Read_Unshowable_RefusedWithOneReasonPerFindingAndItsLine(string provider, string sql, (string, int)[] expected)
    {
        var result = SqlReaders.For(provider).Read(sql);

        Assert.Equal(ReadOutcome.CannotShow, result.Outcome);
        Assert.Null(result.Statement);
        Assert.Equal(expected, result.Reasons.Select(r => (r.Code, r.Line)).ToArray());
    }

    public static TheoryData<string, string, Statement> SingleStatementCases() => new()
    {
        { "postgres", "SELECT ';' AS \"a;b\" -- c;d\n;", new QueryStatement(Query(null, [], new SelectItem(Str(";"), "a;b"))) },
        { "mysql", "SELECT 'it\\'s;' AS `a;b` # c;d\n;\n", new QueryStatement(Query(null, [], new SelectItem(Str("it's;"), "a;b"))) },
        { "sqlite", "SELECT [a;b] FROM t /* ; */;", new QueryStatement(Query(null, [new TableSource(new TableName(null, "t"), null)], new SelectItem(Col("a;b"), null))) },
        { "sqlserver", "SELECT N';' AS \"a;\"\"b\" /* x /* ; */ ; */;", new QueryStatement(Query(null, [], new SelectItem(Str(";"), "a;\"b"))) },
        { "sqlserver", "SELECT [a;]]b] FROM t -- ;\n;", new QueryStatement(Query(null, [new TableSource(new TableName(null, "t"), null)], new SelectItem(Col("a;]b"), null))) },
        { "sqlserver", "SELECT n = [Name] FROM t;", new QueryStatement(Query(null, [new TableSource(new TableName(null, "t"), null)], new SelectItem(Col("Name"), "n"))) },
        { "sqlserver", "SELECT TOP 5 [a] FROM t;", new QueryStatement(new Query(null, new Select(false, [new SelectItem(Col("a"), null)], [new TableSource(new TableName(null, "t"), null)], null, [], null, []), [], new RowLimit(5, null))) },
        { "sqlserver", "SELECT TOP 1 [a] FROM t UNION SELECT [b] FROM u;", new QueryStatement(new Query(null, new SetOperation(new NestedQuery(new Query(null, new Select(false, [new SelectItem(Col("a"), null)], [new TableSource(new TableName(null, "t"), null)], null, [], null, []), [], new RowLimit(1, null))), SetOp.Union, false, new Select(false, [new SelectItem(Col("b"), null)], [new TableSource(new TableName(null, "u"), null)], null, [], null, [])), [], null)) },
    };

    [Theory, MemberData(nameof(SingleStatementCases))]
    public void Read_SeparatorInCommentStringOrIdentifier_IsOneStatement(string provider, string sql, Statement expected)
    {
        var result = SqlReaders.For(provider).Read(sql);

        Assert.Equal(ReadOutcome.Read, result.Outcome);
        Assert.Equal(expected, result.Statement);
    }

    public static TheoryData<string, string, Expr> ExpressionCases() => new()
    {
        { "postgres", "SELECT ÉA", Col("Éa") },
        { "mysql", "SELECT CONCAT(a)", new FunctionCall("CONCAT", [Col("a")], false) },
        { "postgres", "SELECT LOWER(a) LIKE LOWER('x')", new Like(new FunctionCall("LOWER", [Col("a")], false), new FunctionCall("LOWER", [Str("x")], false), false, false) },
        { "postgres", "SELECT SUM(a) OVER W FROM t WINDOW W AS (ORDER BY a)", new WindowCall(new FunctionCall("SUM", [Col("a")], false), new WindowSpec("w", [], [], null)) },
        { "sqlserver", "SELECT N'it''s'", Str("it's") },
        { "sqlserver", "SELECT 'it''s'", new Literal(LiteralKind.NarrowString, "it's") },
        { "sqlserver", "SELECT [a] + N'x'", Bin(BinaryOp.Add, Col("a"), Str("x")) },
        { "sqlserver", "SELECT CONCAT([a], [b])", new Concat([Col("a"), Col("b")]) },
        { "sqlserver", "SELECT Coalesce([a], 0)", new FunctionCall("Coalesce", [Col("a"), Num("0")], false) },
        { "sqlserver", "SELECT IIF([a] = 1, 1, 0)", new Fragment("IIF([a] = 1, 1, 0)") },
        { "sqlserver", "SELECT TRY_CAST([a] AS int)", new Fragment("TRY_CAST([a] AS int)") },
        { "sqlserver", "SELECT CONVERT(varchar(10), [a], 120)", new Fragment("CONVERT(varchar(10), [a], 120)") },
        { "sqlserver", "SELECT CAST([a] AS decimal(10, 2))", new Cast(Col("a"), "decimal(10, 2)") },
        { "sqlserver", "SELECT -12.5", Num("-12.5") },
        { "sqlserver", "SELECT [a] COLLATE Latin1_General_CI_AS", new Fragment("[a] COLLATE Latin1_General_CI_AS") },
        { "sqlserver", "SELECT \"x\"\"y\"", Col("x\"y") },
        { "sqlserver", "SELECT DATEADD(day, 1, [a])", new Fragment("DATEADD(day, 1, [a])") },
    };

    [Theory, MemberData(nameof(ExpressionCases))]
    public void Read_Expression_ReadsAsTheServerMeansIt(string provider, string sql, Expr expected)
    {
        var result = SqlReaders.For(provider).Read(sql);

        var select = Assert.IsType<Select>(Assert.IsType<QueryStatement>(result.Statement).Query.Body);
        Assert.Equal(expected, select.Items[0].Expr);
    }

    public static TheoryData<string, string, int> CommentCases() => new()
    {
        { "postgres", "-- a\nSELECT /* b */ 1 /* c */;", 3 },
        { "mysql", "# a\nSELECT 1 -- b\n;", 2 },
        { "sqlite", "SELECT '--x', '/*y*/';", 0 },
        { "sqlserver", "-- a\nSELECT /* b /* c */ */ 1;", 2 },
    };

    [Theory, MemberData(nameof(CommentCases))]
    public void Read_Comments_AreCounted(string provider, string sql, int expected)
    {
        var result = SqlReaders.For(provider).Read(sql);

        Assert.Equal(expected, result.CommentCount);
    }

    [Fact]
    public void Read_PostgresJsonOperator_KeepsTheExpressionAsAFragment()
    {
        var expected = new QueryStatement(new Query(null, new Select(false, [new SelectItem(new Fragment("\"Data\" ->> 'k'"), null)], [new TableSource(new TableName(null, "T"), null)], null, [], null, []), [], null));

        var result = SqlReaders.For("postgres").Read("SELECT \"Data\" ->> 'k' FROM \"T\";");

        Assert.Equal(ReadOutcome.ReadWithFragments, result.Outcome);
        Assert.Equal(1, result.FragmentCount);
        Assert.Equal(expected, result.Statement);
    }

    public static TheoryData<string, string> FragmentCases() => new()
    {
        { "postgres", "SELECT * FROM t ORDER BY a NULLS FIRST;" },
        { "sqlite", "SELECT CURRENT_TIMESTAMP;" },
        { "postgres", "SELECT * FROM generate_series(1, 3);" },
        { "sqlserver", "SELECT CURRENT_TIMESTAMP;" },
        { "sqlserver", "SELECT * FROM dbo.fn(1);" },
        { "sqlserver", "SELECT * FROM a INNER LOOP JOIN b ON a.x = b.x;" },
        { "sqlserver", "SELECT * FROM db.dbo.t;" },
        { "sqlserver", "SELECT 1 WHERE [a] LIKE N'x!%' ESCAPE N'!';" },
    };

    [Theory, MemberData(nameof(FragmentCases))]
    public void Read_NodeWithoutAModelMapping_IsCountedAsOneFragment(string provider, string sql)
    {
        var result = SqlReaders.For(provider).Read(sql);

        Assert.Equal(ReadOutcome.ReadWithFragments, result.Outcome);
        Assert.Equal(1, result.FragmentCount);
    }

    [Fact]
    public void Read_ParseError_CannotShowWithTheParserMessageAndLine()
    {
        var result = SqlReaders.For("postgres").Read("SELECT 1\nFROM t WHERE;");

        var reason = Assert.Single(result.Reasons);
        Assert.Equal(ReadOutcome.CannotShow, result.Outcome);
        Assert.Equal(("parse-error", 2), (reason.Code, reason.Line));
        Assert.StartsWith("Expected", reason.Message);
    }

    [Fact]
    public void Read_PostgresUnquotedIdentifiers_FoldToLowerCase()
    {
        var expected = new QueryStatement(new Query(null, new Select(false, [new SelectItem(new ColumnRef("r", "name"), "fullname")], [new TableSource(new TableName("public", "researcher"), "r")], null, [], null, []), [], null));

        var result = SqlReaders.For("postgres").Read("SELECT R.Name AS FullName FROM Public.Researcher R");

        Assert.Equal(expected, result.Statement);
    }

    public static TheoryData<string, Expr> PostgresPrecedenceCases() => new()
    {
        { "SELECT 1 WHERE NOT a = 1 AND b", new Binary(BinaryOp.And, new Unary(UnaryOp.Not, new Binary(BinaryOp.Eq, new ColumnRef(null, "a"), new Literal(LiteralKind.Number, "1"))), new ColumnRef(null, "b")) },
        { "SELECT 1 WHERE -a < b", new Binary(BinaryOp.Lt, new Unary(UnaryOp.Negate, new ColumnRef(null, "a")), new ColumnRef(null, "b")) },
    };

    [Theory, MemberData(nameof(PostgresPrecedenceCases))]
    public void Read_PostgresPrefixOperator_BindsAsPostgresDoes(string sql, Expr expected)
    {
        var result = SqlReaders.For("postgres").Read(sql);

        var select = Assert.IsType<Select>(Assert.IsType<QueryStatement>(result.Statement).Query.Body);
        Assert.Equal(expected, select.Where);
    }

    [Fact]
    public void Read_SqlServerTableHint_KeepsTheTableAsAFragmentVerbatim()
    {
        var on = Bin(BinaryOp.Eq, Col("u", "id"), Col("t", "id"));
        var from = new JoinSource(new FragmentSource("t AS x WITH (NOLOCK)"), JoinType.Inner, new TableSource(new TableName(null, "u"), null), on);
        var expected = new QueryStatement(Query(null, [from], new SelectItem(Col("a"), null)));

        var result = SqlReaders.For("sqlserver").Read("SELECT a FROM t AS x WITH (NOLOCK)\nINNER JOIN u ON u.id = t.id;");

        Assert.Equal(ReadOutcome.ReadWithFragments, result.Outcome);
        Assert.Equal(expected, result.Statement);
    }

    public static TheoryData<string, Expr> SqlServerPredicateCases() => new()
    {
        { "SELECT 1 WHERE 1 = 1", new Literal(LiteralKind.Boolean, "true") },
        { "SELECT 1 WHERE (1 = 0)", new Literal(LiteralKind.Boolean, "false") },
        { "SELECT 1 WHERE [a] = 1", Bin(BinaryOp.Eq, Col("a"), Num("1")) },
        { "SELECT 1 WHERE NOT EXISTS (SELECT 1)", new Exists(Query(Num("1")), true) },
        { "SELECT 1 WHERE [a] != 1", Bin(BinaryOp.NotEq, Col("a"), Num("1")) },
    };

    [Theory, MemberData(nameof(SqlServerPredicateCases))]
    public void Read_SqlServerPredicate_ReadsAsTheGeneratorWritesIt(string sql, Expr expected)
    {
        var result = SqlReaders.For("sqlserver").Read(sql);

        var select = Assert.IsType<Select>(Assert.IsType<QueryStatement>(result.Statement).Query.Body);
        Assert.Equal(expected, select.Where);
    }
}
