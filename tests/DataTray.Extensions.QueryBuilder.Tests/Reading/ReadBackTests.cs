using DataTray.Extensions.QueryBuilder.Dialects;
using DataTray.Extensions.QueryBuilder.Model;
using DataTray.Extensions.QueryBuilder.Reading;
using static DataTray.Extensions.QueryBuilder.Tests.Generation.Build;

namespace DataTray.Extensions.QueryBuilder.Tests.Reading;

public class ReadBackTests
{
    public static TheoryData<string, string> ReformattedCases() => new()
    {
        { "sqlserver", "select r.Name n\nfrom dbo.Researcher r -- who\nleft outer join Institution i on r.InstitutionId = i.Id\nwhere r.Id != 1\norder by r.Id asc" },
        { "sqlserver", "SELECT TOP 5 Name FROM Researcher;" },
        { "sqlserver", "SELECT a FROM t JOIN u ON t.x = u.x" },
        { "sqlserver", "SELECT N'it''s' AS [x y], 'narrow' AS z /* c */;" },
        { "postgres", "select R.Name FullName\nfrom Researcher R\n  left outer join Institution I on R.InstitutionId = I.Id\nwhere R.Id != 1 /* c */\norder by R.Id asc;" },
        { "postgres", "SELECT a FROM t LIMIT ALL" },
        { "postgres", "SELECT \"a\" FROM \"t\" WHERE \"a\" = 'x'" },
        { "mysql", "select a b from t left outer join u on t.x = u.x where a != 1 order by a asc limit 20, 10" },
        { "mysql", "SELECT 'it\\'s' AS s; # done" },
        { "sqlite", "select [a] from `t` join u on t.x = u.x;\n-- trailing" },
        { "sqlite", "SELECT a AS \"b\" FROM t WHERE a != 1" },
    };

    [Theory, MemberData(nameof(ReformattedCases))]
    public void ReadBack_ReformattedSameMeaning_Accepts(string provider, string sql)
    {
        var result = ReadBack.ReadAndVerify(sql, provider, null);

        Assert.Empty(result.Reasons);
        Assert.Equal(ReadOutcome.Read, result.Outcome);
    }

    public static TheoryData<string> LineBreakCases() => ["SELECT [a]\nFROM [t];", "SELECT [a]\r\nFROM [t];", "SELECT [a]\rFROM [t];"];

    [Theory, MemberData(nameof(LineBreakCases))]
    public void ReadBack_DifferentMeaning_RefusesNamingTheFirstDifferenceAndItsLine(string sql)
    {
        var lossy = new QueryStatement(Query(null, [new TableSource(new TableName(null, "u"), null)], new SelectItem(Col("a"), null)));

        var result = ReadBack.ReadAndVerify(sql, new FixedReader(lossy), SqlDialects.For("sqlserver", null));

        var reason = Assert.Single(result.Reasons);
        Assert.Equal((ReadOutcome.CannotShow, "would-change", 2), (result.Outcome, reason.Code, reason.Line));
        Assert.Contains("`T` would become `U`", reason.Message);
    }

    public static TheoryData<string, string, Statement> LossyReadCases() => new()
    {
        { "sqlserver", "SELECT CURRENT_TIMESTAMP;", new QueryStatement(Query(Col("CURRENT_TIMESTAMP"))) },
        { "sqlserver", "SELECT N'x';", new QueryStatement(Query(new Literal(LiteralKind.NarrowString, "x"))) },
        { "postgres", "SELECT \"Name\";", new QueryStatement(Query(Col("name"))) },
        { "mysql", "SELECT 'a\\\\b';", new QueryStatement(Query(Str("a\\\\b"))) },
        { "sqlite", "SELECT a FROM t ORDER BY a DESC;", new QueryStatement(new Query(null, new Select(false, [new SelectItem(Col("a"), null)], [new TableSource(new TableName(null, "t"), null)], null, [], null, []), [new OrderItem(Col("a"), false)], null)) },
    };

    [Theory, MemberData(nameof(LossyReadCases))]
    public void ReadBack_ModelThatMeansSomethingElse_Refuses(string provider, string sql, Statement lossy)
    {
        var result = ReadBack.ReadAndVerify(sql, new FixedReader(lossy), SqlDialects.For(provider, null));

        Assert.Equal((ReadOutcome.CannotShow, "would-change"), (result.Outcome, Assert.Single(result.Reasons).Code));
    }

    public static TheoryData<string, string, int> WrittenDifferentlyCases() => new()
    {
        { "sqlserver", "SELECT n = [Name] FROM t;", 1 },
        { "sqlserver", "SELECT a FROM t\nORDER BY a\nOFFSET 0 ROWS FETCH FIRST 5 ROW ONLY;", 3 },
        { "postgres", "SELECT a\nFROM t\nWHERE (a = 1);", 3 },
        { "postgres", "SELECT a::int FROM t;", 1 },
        { "postgres", "SELECT 1 -- c\r+ 2", 2 },
    };

    [Theory, MemberData(nameof(WrittenDifferentlyCases))]
    public void ReadBack_ReaderWouldWriteItDifferently_RefusesAsWouldChange(string provider, string sql, int line)
    {
        var result = ReadBack.ReadAndVerify(sql, provider, null);

        var reason = Assert.Single(result.Reasons);
        Assert.Equal((ReadOutcome.CannotShow, "would-change", line), (result.Outcome, reason.Code, reason.Line));
    }

    // The server lexes these otherwise than the parser: PostgreSQL ends "--" at \r, only ASCII whitespace separates
    // words, and a number glued to letters, digits or _ is one (bad) token, never a number plus an alias.
    public static TheoryData<string, string> ServerLexesDifferentlyCases() => new()
    {
        { "postgres", "SELECT a\u00A0b FROM t" },
        { "postgres", "SELECT a,\u00A0b FROM t" },
        { "postgres", "SELECT 1 FROM t WHERE a =\u00A01" },
        { "postgres", "SELECT a\u2003b FROM t" },
        { "postgres", "SELECT a\u0085b FROM t" },
        { "postgres", "SELECT 1 FROM t WHERE a\u00A0= 1" },
        { "sqlite", "SELECT a\u00A0b FROM t" },
        { "mysql", "SELECT a\u00A0b FROM t" },
        { "postgres", "SELECT 1_000" },
        { "postgres", "SELECT 0b101" },
        { "postgres", "SELECT 0o17" },
        { "postgres", "SELECT 1a" },
        { "postgres", "SELECT 1.5e" },
        { "sqlite", "SELECT 1a" },
        { "sqlite", "SELECT 1_000" },
        { "sqlite", "SELECT 1.5e" },
        { "mysql", "SELECT 1a" },
    };

    [Theory, MemberData(nameof(ServerLexesDifferentlyCases))]
    public void ReadBack_TextTheServerLexesDifferently_CannotShow(string provider, string sql)
    {
        var result = ReadBack.ReadAndVerify(sql, provider, null);

        Assert.Equal(ReadOutcome.CannotShow, result.Outcome);
    }

    [Fact]
    public void ReadBack_ModelTheDialectCannotWrite_CannotShowWithTheProblem()
    {
        var full = new JoinSource(new TableSource(new TableName(null, "t"), null), JoinType.Full, new TableSource(new TableName(null, "u"), null), Num("1"));
        var statement = new QueryStatement(Query(null, [full], new SelectItem(Col("a"), null)));

        var result = ReadBack.ReadAndVerify("SELECT a FROM t FULL JOIN u ON 1", new FixedReader(statement), SqlDialects.For("mysql", null));

        var reason = Assert.Single(result.Reasons);
        Assert.Equal((ReadOutcome.CannotShow, "cannot-generate", 1), (result.Outcome, reason.Code, reason.Line));
        Assert.Contains("full-join", reason.Message);
    }

    [Theory, MemberData(nameof(SqlReaderTests.UnshowableCases), MemberType = typeof(SqlReaderTests))]
    public void ReadBack_Unshowable_KeepsTheReadersReasons(string provider, string sql, (string, int)[] expected)
    {
        var result = ReadBack.ReadAndVerify(sql, provider, null);

        Assert.Equal(ReadOutcome.CannotShow, result.Outcome);
        Assert.Equal(expected, result.Reasons.Select(r => (r.Code, r.Line)).ToArray());
    }

    public static TheoryData<string, string> MoreFragmentCases() => new()
    {
        { "postgres", "SELECT \"Data\" ->> 'k' FROM \"T\";" },
        { "sqlserver", "SELECT IIF([a] = 1, 1, 0)" },
        { "sqlserver", "SELECT TRY_CAST([a] AS int)" },
        { "sqlserver", "SELECT CONVERT(varchar(10), [a], 120)" },
        { "sqlserver", "SELECT [a] COLLATE Latin1_General_CI_AS" },
        { "sqlserver", "SELECT DATEADD(day, 1, [a])" },
        { "sqlserver", "SELECT TRIM(N'x' FROM [a])" },
        { "sqlserver", "SELECT 1e" },
        { "sqlserver", "SELECT a FROM t AS x WITH (NOLOCK)\nINNER JOIN u ON u.id = t.id;" },
    };

    [Theory, MemberData(nameof(SqlReaderTests.FragmentCases), MemberType = typeof(SqlReaderTests)), MemberData(nameof(MoreFragmentCases))]
    public void ReadBack_Fragments_AreWrittenVerbatimAndVerify(string provider, string sql)
    {
        var result = ReadBack.ReadAndVerify(sql, provider, null);

        Assert.Empty(result.Reasons);
        Assert.Equal(ReadOutcome.ReadWithFragments, result.Outcome);
    }

    [Fact]
    public void ReadBack_Accepted_KeepsTheReadersCounts()
    {
        var result = ReadBack.ReadAndVerify("-- a\nSELECT /* b */ \"Data\" ->> 'k' FROM \"T\";", "postgres", null);

        Assert.Equal((ReadOutcome.ReadWithFragments, 1, 2), (result.Outcome, result.FragmentCount, result.CommentCount));
    }
}
