using DataTray.Extensions.QueryBuilder.Model;
using static DataTray.Extensions.QueryBuilder.Tests.Generation.Build;

namespace DataTray.Extensions.QueryBuilder.Tests.Generation;

public class ExpressionGenerationTests
{
    public static TheoryData<string, Expr, string> PrecedenceCases() => new()
    {
        { "postgres", Bin(BinaryOp.Or, Col("a"), Bin(BinaryOp.And, Col("b"), Col("c"))), "SELECT \"a\" OR \"b\" AND \"c\";" },
        { "postgres", Bin(BinaryOp.And, Bin(BinaryOp.Or, Col("a"), Col("b")), Col("c")), "SELECT (\"a\" OR \"b\") AND \"c\";" },
        { "postgres", Not(Bin(BinaryOp.And, Col("a"), Col("b"))), "SELECT NOT (\"a\" AND \"b\");" },
        { "postgres", Bin(BinaryOp.And, Not(Col("a")), Col("b")), "SELECT NOT \"a\" AND \"b\";" },
        { "postgres", Not(Bin(BinaryOp.Eq, Col("a"), Col("b"))), "SELECT NOT \"a\" = \"b\";" },
        { "sqlserver", Bin(BinaryOp.Eq, Bin(BinaryOp.Add, Col("a"), Num("1")), Bin(BinaryOp.Multiply, Col("b"), Num("2"))), "SELECT [a] + 1 = [b] * 2;" },
        { "mysql", Bin(BinaryOp.Multiply, Bin(BinaryOp.Add, Col("a"), Col("b")), Col("c")), "SELECT (`a` + `b`) * `c`;" },
        { "sqlite", Bin(BinaryOp.Subtract, Col("a"), Bin(BinaryOp.Subtract, Col("b"), Col("c"))), "SELECT \"a\" - (\"b\" - \"c\");" },
        { "sqlite", Bin(BinaryOp.Subtract, Bin(BinaryOp.Subtract, Col("a"), Col("b")), Col("c")), "SELECT \"a\" - \"b\" - \"c\";" },
        { "postgres", Bin(BinaryOp.Divide, Col("a"), Bin(BinaryOp.Multiply, Col("b"), Col("c"))), "SELECT \"a\" / (\"b\" * \"c\");" },
        { "postgres", Bin(BinaryOp.Multiply, Col("a"), Bin(BinaryOp.Divide, Col("b"), Col("c"))), "SELECT \"a\" * (\"b\" / \"c\");" },
        { "postgres", Bin(BinaryOp.Modulo, Col("a"), Bin(BinaryOp.Modulo, Col("b"), Col("c"))), "SELECT \"a\" % (\"b\" % \"c\");" },
        { "postgres", Bin(BinaryOp.And, Col("a"), Bin(BinaryOp.And, Col("b"), Col("c"))), "SELECT \"a\" AND (\"b\" AND \"c\");" },
        { "postgres", Bin(BinaryOp.And, Bin(BinaryOp.And, Col("a"), Col("b")), Col("c")), "SELECT \"a\" AND \"b\" AND \"c\";" },
        { "postgres", Bin(BinaryOp.Add, Col("a"), Bin(BinaryOp.Add, Col("b"), Col("c"))), "SELECT \"a\" + (\"b\" + \"c\");" },
        { "postgres", Bin(BinaryOp.Eq, Bin(BinaryOp.Eq, Col("a"), Col("b")), Col("c")), "SELECT (\"a\" = \"b\") = \"c\";" },
        { "postgres", Neg(Bin(BinaryOp.Add, Col("a"), Col("b"))), "SELECT -(\"a\" + \"b\");" },
        { "postgres", Neg(Neg(Col("a"))), "SELECT -(-\"a\");" },
        { "postgres", Neg(Num("-1")), "SELECT -(-1);" },
        { "postgres", Bin(BinaryOp.Subtract, Col("a"), Num("-1")), "SELECT \"a\" - -1;" },
        { "postgres", new Between(Col("a"), Bin(BinaryOp.Add, Col("b"), Num("1")), Col("c"), false), "SELECT \"a\" BETWEEN \"b\" + 1 AND \"c\";" },
        { "postgres", new Between(Col("a"), Bin(BinaryOp.And, Col("b"), Col("c")), Col("d"), true), "SELECT \"a\" NOT BETWEEN (\"b\" AND \"c\") AND \"d\";" },
        { "postgres", Bin(BinaryOp.And, new Between(Col("a"), Col("b"), Col("c"), false), Col("d")), "SELECT \"a\" BETWEEN \"b\" AND \"c\" AND \"d\";" },
        { "postgres", new IsNull(Bin(BinaryOp.Add, Col("a"), Col("b")), false), "SELECT \"a\" + \"b\" IS NULL;" },
        { "postgres", new IsNull(Bin(BinaryOp.Eq, Col("a"), Col("b")), true), "SELECT (\"a\" = \"b\") IS NOT NULL;" },
        { "postgres", Bin(BinaryOp.And, new Fragment("x OR y"), Col("a")), "SELECT (x OR y) AND \"a\";" },
        { "postgres", Not(new InList(Col("a"), [Num("1"), Bin(BinaryOp.Or, Col("b"), Col("c"))], false)), "SELECT NOT \"a\" IN (1, \"b\" OR \"c\");" },
    };

    [Theory, MemberData(nameof(PrecedenceCases))]
    public void Generate_ParenthesizesOnlyWhereTheTreeRequires(string provider, Expr expr, string expected)
    {
        Assert.Equal(expected, Generate(provider, null, expr).Sql);
    }

    public static TheoryData<string, Expr, string> EscapingCases() => new()
    {
        { "sqlserver", Col("a]b"), "SELECT [a]]b];" },
        { "postgres", Col("a\"b"), "SELECT \"a\"\"b\";" },
        { "mysql", Col("a`b"), "SELECT `a``b`;" },
        { "sqlite", Col("a\"b"), "SELECT \"a\"\"b\";" },
        { "sqlserver", Str("it's\\\nx"), "SELECT N'it''s\\\nx';" },
        { "postgres", Str("it's\\\nx"), "SELECT 'it''s\\\nx';" },
        { "mysql", Str("it's\\\nx"), "SELECT 'it''s\\\\\nx';" },
        { "sqlite", Str("it's\\\nx"), "SELECT 'it''s\\\nx';" },
        { "sqlserver", Col("r", "FullName"), "SELECT r.[FullName];" },
        { "postgres", Col("my alias", "x"), "SELECT \"my alias\".\"x\";" },
        { "mysql", Col("select", "x"), "SELECT `select`.`x`;" },
        { "sqlite", Col("a\"b", "x"), "SELECT \"a\"\"b\".\"x\";" },
        { "sqlserver", new Star("r"), "SELECT r.*;" },
        { "postgres", new Star(null), "SELECT *;" },
    };

    [Theory, MemberData(nameof(EscapingCases))]
    public void Generate_EscapesNamesAndStringsPerDialect(string provider, Expr expr, string expected)
    {
        Assert.Equal(expected, Generate(provider, null, expr).Sql);
    }

    [Theory]
    [InlineData("sqlserver", "a]b", "SELECT 1 AS [a]]b];")]
    [InlineData("mysql", "a`b", "SELECT 1 AS `a``b`;")]
    public void Generate_AlwaysQuotesOutputAliases(string provider, string alias, string expected)
    {
        var query = Query(null, [], new SelectItem(Num("1"), alias));

        Assert.Equal(expected, Generate(provider, null, query).Sql);
    }

    [Theory]
    [InlineData("42", "SELECT 42;")]
    [InlineData("-2.5", "SELECT -2.5;")]
    [InlineData(".5", "SELECT .5;")]
    [InlineData("1.5e-3", "SELECT 1.5e-3;")]
    public void Generate_WritesNumbersVerbatim(string text, string expected)
    {
        Assert.Equal(expected, Generate("postgres", null, Num(text)).Sql);
    }

    [Theory]
    [InlineData("1; DROP TABLE t")]
    [InlineData("1\n")]
    [InlineData("0x1F")]
    [InlineData("1,5")]
    [InlineData("")]
    public void Generate_InvalidNumber_IsAProblem(string text)
    {
        var result = Generate("postgres", null, Num(text));

        Assert.Null(result.Sql);
        Assert.Equal(["invalid-number"], Codes(result));
    }

    [Theory]
    [InlineData("sqlserver", null, "true", "SELECT 1;")]
    [InlineData("postgres", null, "false", "SELECT FALSE;")]
    [InlineData("mysql", null, "TRUE", "SELECT TRUE;")]
    [InlineData("sqlite", null, "true", "SELECT 1;")]
    [InlineData("sqlite", "3.23.0", "true", "SELECT TRUE;")]
    public void Generate_BooleansFollowTheDialect(string provider, string? version, string text, string expected)
    {
        Assert.Equal(expected, Generate(provider, version, new Literal(LiteralKind.Boolean, text)).Sql);
    }

    public static TheoryData<string, string?, Query, string> BooleanPredicateCases() => new()
    {
        { "sqlserver", null, Query(new Literal(LiteralKind.Boolean, "true"), [], new SelectItem(Num("1"), null)), "SELECT 1\nWHERE 1 = 1;" },
        { "sqlite", null, Query(new Literal(LiteralKind.Boolean, "false"), [], new SelectItem(Num("1"), null)), "SELECT 1\nWHERE 1 = 0;" },
        { "postgres", null, Query(new Literal(LiteralKind.Boolean, "true"), [], new SelectItem(Num("1"), null)), "SELECT 1\nWHERE TRUE;" },
        { "sqlserver", null, Query(Not(new Literal(LiteralKind.Boolean, "true"))), "SELECT NOT 1 = 1;" },
        { "sqlserver", null, Query(Bin(BinaryOp.Or, Bin(BinaryOp.Eq, Col("a"), Num("1")), new Literal(LiteralKind.Boolean, "false"))), "SELECT [a] = 1 OR 1 = 0;" },
        { "sqlserver", null, Query(new Case(null, [new CaseWhen(new Literal(LiteralKind.Boolean, "true"), Num("1"))], null)), "SELECT CASE WHEN 1 = 1 THEN 1 END;" },
        { "sqlserver", null, Query(new Case(Col("a"), [new CaseWhen(new Literal(LiteralKind.Boolean, "true"), Num("1"))], null)), "SELECT CASE [a] WHEN 1 THEN 1 END;" },
        { "sqlserver", null, Query(Bin(BinaryOp.Eq, Col("a"), new Literal(LiteralKind.Boolean, "true"))), "SELECT [a] = 1;" },
        { "sqlserver", null, Query(null, [new JoinSource(Table("A", "a"), JoinType.Inner, Table("B", "b"), new Literal(LiteralKind.Boolean, "true"))], new SelectItem(new Star(null), null)), "SELECT *\nFROM [dbo].[A] AS a\nINNER JOIN [dbo].[B] AS b ON 1 = 1;" },
    };

    [Theory, MemberData(nameof(BooleanPredicateCases))]
    public void Generate_BooleanAsPredicate_IsAComparisonWithoutBooleanLiterals(string provider, string? version, Query query, string expected)
    {
        Assert.Equal(expected, Generate(provider, version, query).Sql);
    }

    [Fact]
    public void Generate_InvalidBoolean_IsAProblem()
    {
        var result = Generate("postgres", null, new Literal(LiteralKind.Boolean, "yes"));

        Assert.Null(result.Sql);
        Assert.Equal(["invalid-boolean"], Codes(result));
    }

    public static TheoryData<string, Expr, string> ConcatCases() => new()
    {
        { "sqlserver", new Concat([Col("a"), Str("x"), Bin(BinaryOp.Add, Col("b"), Num("1"))]), "SELECT CONCAT([a], N'x', [b] + 1);" },
        { "postgres", new Concat([Col("a"), Str("x"), Bin(BinaryOp.Add, Col("b"), Num("1"))]), "SELECT \"a\" || 'x' || (\"b\" + 1);" },
        { "mysql", new Concat([Col("a"), Str("x"), Bin(BinaryOp.Add, Col("b"), Num("1"))]), "SELECT CONCAT(`a`, 'x', `b` + 1);" },
        { "sqlite", new Concat([Col("a"), Str("x"), Bin(BinaryOp.Add, Col("b"), Num("1"))]), "SELECT \"a\" || 'x' || (\"b\" + 1);" },
        { "postgres", Bin(BinaryOp.Eq, new Concat([Col("a"), Col("b")]), Str("ab")), "SELECT \"a\" || \"b\" = 'ab';" },
        { "sqlite", Bin(BinaryOp.Add, new Concat([Col("a"), Col("b")]), Num("1")), "SELECT (\"a\" || \"b\") + 1;" },
        { "mysql", Bin(BinaryOp.Add, new Concat([Col("a"), Col("b")]), Num("1")), "SELECT CONCAT(`a`, `b`) + 1;" },
    };

    [Theory, MemberData(nameof(ConcatCases))]
    public void Generate_ConcatFollowsTheDialect(string provider, Expr expr, string expected)
    {
        Assert.Equal(expected, Generate(provider, null, expr).Sql);
    }

    [Theory]
    [InlineData("sqlserver", true, false, "SELECT LOWER([a]) LIKE LOWER(N'x%');")]
    [InlineData("postgres", true, false, "SELECT \"a\" ILIKE 'x%';")]
    [InlineData("postgres", true, true, "SELECT \"a\" NOT ILIKE 'x%';")]
    [InlineData("mysql", true, true, "SELECT LOWER(`a`) NOT LIKE LOWER('x%');")]
    [InlineData("sqlite", true, false, "SELECT LOWER(\"a\") LIKE LOWER('x%');")]
    [InlineData("sqlite", false, false, "SELECT \"a\" LIKE 'x%';")]
    [InlineData("sqlserver", false, true, "SELECT [a] NOT LIKE N'x%';")]
    public void Generate_LikeFollowsTheDialect(string provider, bool caseInsensitive, bool not, string expected)
    {
        Assert.Equal(expected, Generate(provider, null, new Like(Col("a"), Str("x%"), caseInsensitive, not)).Sql);
    }

    public static TheoryData<string, Expr, string> SyntaxCases() => new()
    {
        { "sqlserver", new Literal(LiteralKind.Null, "NULL"), "SELECT NULL;" },
        { "sqlserver", new FunctionCall("COUNT", [new Star(null)], false), "SELECT COUNT(*);" },
        { "postgres", new FunctionCall("count", [Col("a")], true), "SELECT count(DISTINCT \"a\");" },
        { "mysql", new FunctionCall("COALESCE", [Col("a"), Num("0")], false), "SELECT COALESCE(`a`, 0);" },
        { "sqlserver", new Cast(Col("a"), "nvarchar(20)"), "SELECT CAST([a] AS nvarchar(20));" },
        { "postgres", new Case(null, [new CaseWhen(Bin(BinaryOp.Gt, Col("a"), Num("1")), Str("big"))], Str("small")), "SELECT CASE WHEN \"a\" > 1 THEN 'big' ELSE 'small' END;" },
        { "sqlite", new Case(Col("a"), [new CaseWhen(Num("1"), Str("one")), new CaseWhen(Num("2"), Str("two"))], null), "SELECT CASE \"a\" WHEN 1 THEN 'one' WHEN 2 THEN 'two' END;" },
        { "mysql", new InList(Col("a"), [Num("1"), Num("2")], true), "SELECT `a` NOT IN (1, 2);" },
        { "postgres", Bin(BinaryOp.NotEq, Col("a"), Num("1")), "SELECT \"a\" <> 1;" },
        { "postgres", Bin(BinaryOp.LtEq, Col("a"), Col("b")), "SELECT \"a\" <= \"b\";" },
        { "postgres", Bin(BinaryOp.GtEq, Col("a"), Col("b")), "SELECT \"a\" >= \"b\";" },
        { "postgres", Bin(BinaryOp.Lt, Col("a"), Col("b")), "SELECT \"a\" < \"b\";" },
        { "postgres", Bin(BinaryOp.Modulo, Col("a"), Num("2")), "SELECT \"a\" % 2;" },
        { "postgres", new Fragment("now() - interval '1 day'"), "SELECT now() - interval '1 day';" },
    };

    [Theory, MemberData(nameof(SyntaxCases))]
    public void Generate_WritesEachExpression(string provider, Expr expr, string expected)
    {
        Assert.Equal(expected, Generate(provider, null, expr).Sql);
    }
}
