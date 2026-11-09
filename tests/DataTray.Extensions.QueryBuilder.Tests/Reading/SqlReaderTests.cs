using DataTray.Extensions.QueryBuilder.Model;
using DataTray.Extensions.QueryBuilder.Reading;

namespace DataTray.Extensions.QueryBuilder.Tests.Reading;

public class SqlReaderTests
{
    public static TheoryData<string, string, (string, int)[]> UnshowableCases() => new()
    {
        { "postgres", "SELECT 1;\nSELECT 2;", [("multiple-statements", 2)] },
        { "postgres", "BEGIN;\nSELECT 1;\nCOMMIT;", [("transaction", 1), ("multiple-statements", 2), ("transaction", 3)] },
        { "mysql", "SET @x = 1;\n\nSELECT @x;", [("set-variable", 1), ("multiple-statements", 3)] },
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
    };

    [Theory, MemberData(nameof(UnshowableCases))]
    public void Read_Unshowable_RefusedWithOneReasonPerFindingAndItsLine(string provider, string sql, (string, int)[] expected)
    {
        var result = SqlReaders.For(provider).Read(sql);

        Assert.Equal(ReadOutcome.CannotShow, result.Outcome);
        Assert.Null(result.Statement);
        Assert.Equal(expected, result.Reasons.Select(r => (r.Code, r.Line)).ToArray());
    }

    public static TheoryData<string, string> SingleStatementCases() => new()
    {
        { "postgres", "SELECT ';' AS \"a;b\" -- c;d\n;" },
        { "mysql", "SELECT 'it\\'s;' AS `a;b` # c;d\n;\n" },
        { "sqlite", "SELECT [a;b] FROM t /* ; */;" },
    };

    [Theory, MemberData(nameof(SingleStatementCases))]
    public void Read_SeparatorInCommentStringOrIdentifier_IsOneStatement(string provider, string sql)
    {
        var result = SqlReaders.For(provider).Read(sql);

        Assert.Equal(ReadOutcome.Read, result.Outcome);
    }

    public static TheoryData<string, string, int> CommentCases() => new()
    {
        { "postgres", "-- a\nSELECT /* b */ 1 /* c */;", 3 },
        { "mysql", "# a\nSELECT 1 -- b\n;", 2 },
        { "sqlite", "SELECT '--x', '/*y*/';", 0 },
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
        { "mysql", "SELECT a || b FROM t;" },
        { "postgres", "SELECT * FROM t ORDER BY a NULLS FIRST;" },
        { "sqlite", "SELECT CURRENT_TIMESTAMP;" },
        { "postgres", "SELECT * FROM generate_series(1, 3);" },
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
    public void For_SqlServer_IsNotSupportedYet()
    {
        Assert.Throws<NotSupportedException>(() => SqlReaders.For("sqlserver"));
    }
}
