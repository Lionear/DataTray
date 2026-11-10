using System.Text.Json.Nodes;
using DataTray.Extensions.QueryBuilder.Model;

namespace DataTray.Extensions.QueryBuilder.Tests.Model;

public class QueryModelTests
{
    private static ColumnRef Col(string name) => new(null, name);

    private static Literal Num(string text) => new(LiteralKind.Number, text);

    private static Query Simple(Expr item) =>
        new(null, new Select(false, [new SelectItem(item, null)], [], null, [], null, []), [], null);

    public static TheoryData<Statement> EveryNode()
    {
        var table = new TableName("dbo", "t");
        var call = new FunctionCall("SUM", [Col("a")], true);
        var spec = new WindowSpec(
            "w",
            [Col("p")],
            [new OrderItem(Col("o"), true)],
            new Frame(FrameUnit.Rows, new FrameBound(BoundKind.Preceding, 2), new FrameBound(BoundKind.CurrentRow, null), FrameExclude.Ties));
        Expr[] expressions =
        [
            Col("a"), new Star("t"), new Literal(LiteralKind.String, "x"), new Literal(LiteralKind.Boolean, "true"), new Literal(LiteralKind.Null, "NULL"),
            new Unary(UnaryOp.Not, Col("b")), new Unary(UnaryOp.Negate, Num("1")),
            new Binary(BinaryOp.Or, Col("a"), new Binary(BinaryOp.Modulo, Num("5"), Num("2"))),
            new Between(Col("a"), Num("1"), Num("9"), true), new Like(Col("a"), new Literal(LiteralKind.String, "%x"), true, false),
            new IsNull(Col("a"), true), new InList(Col("a"), [Num("1"), Num("2")], false),
            new InSubquery(Col("a"), Simple(Num("1")), true), new Exists(Simple(Num("1")), false),
            new Quantified(Col("a"), BinaryOp.GtEq, Quantifier.Any, Simple(Num("1"))), new ScalarSubquery(Simple(Num("1"))),
            call, new WindowCall(call, spec),
            new Case(Col("a"), [new CaseWhen(Num("1"), Num("2"))], Num("3")), new Cast(Col("a"), "int"),
            new Concat([Col("a"), Col("b")]), new Fragment("raw()"),
        ];
        Source[] sources =
        [
            new TableSource(table, "x"), new CteSource("c", null), new SubquerySource(Simple(Num("1")), "s", true),
            new JoinSource(new TableSource(table, null), JoinType.CrossApply, new FragmentSource("f()"), Col("a")),
        ];
        var select = new Select(true, [new SelectItem(Col("a"), "z")], sources, Col("w"), [Col("g")], Col("h"), [new NamedWindow("w", spec)]);
        var body = new SetOperation(
            select,
            SetOp.Except,
            true,
            new SetOperation(new ValuesBody([[Num("1")]]), SetOp.Union, false, new NestedQuery(Simple(Num("1")))));
        var query = new Query(
            new With(true, [new Cte("c", ["k"], Simple(new InList(Col("a"), expressions, false)))]),
            body,
            [new OrderItem(Col("a"), false)],
            new RowLimit(10, 5));
        var assignments = new ValueList<Assignment>([new Assignment("a", Num("1"))]);
        var returning = new Returning([new SelectItem(new Star(null), null)]);
        var merge = new MergeStatement(
            table, "m", new TableSource(table, "u"), Col("on"),
            [
                new MergeClause(MergeMatch.Matched, Col("a"), new MergeUpdate(assignments)),
                new MergeClause(MergeMatch.NotMatched, null, new MergeInsert(["a"], [Num("1")])),
                new MergeClause(MergeMatch.NotMatchedBySource, null, new MergeDelete()),
            ],
            returning);
        return
        [
            new QueryStatement(query),
            new InsertStatement(null, table, ["a"], new ValuesSource([[Num("1"), Num("2")]]), new Upsert(["a"], assignments), returning),
            new InsertStatement(null, table, ["a"], new QuerySource(query), new Upsert(["a"], null), null),
            new InsertStatement(null, table, [], new DefaultValuesSource(), null, null),
            new UpdateStatement(null, table, "u", assignments, sources, Col("a"), returning),
            new DeleteStatement(null, table, "d", sources, Col("a"), returning),
            merge,
        ];
    }

    [Fact]
    public void Equality_IsStructural_ForLists()
    {
        Query Build() => new(
            null,
            new Select(false, [new SelectItem(Col("a"), null)], [new TableSource(new TableName(null, "t"), null)], null, [], null, []),
            [new OrderItem(Col("a"), false)],
            null);
        var rows1 = new ValuesBody([[Num("1"), Num("2")]]);
        var rows2 = new ValuesBody([[Num("1"), Num("2")]]);

        Assert.Equal(new object[] { Build(), rows1 }, new object[] { Build(), rows2 });
        Assert.Equal(Build().GetHashCode(), Build().GetHashCode());
    }

    [Fact]
    public void Json_Serialize_WritesFormatVersionOne()
    {
        var json = QueryModelJson.Serialize(new QueryStatement(Simple(Col("a"))));

        Assert.Equal(1, JsonNode.Parse(json)?["formatVersion"]?.GetValue<int>());
    }

    [Fact]
    public void Json_Deserialize_TypeDiscriminatorAfterOtherProperties_Reads()
    {
        const string json = """
            {
              "query": {
                "body": { "distinct": false, "items": [ { "expr": { "name": "a", "$type": "ColumnRef" } } ], "from": [], "groupBy": [], "windows": [], "$type": "Select" },
                "orderBy": []
              },
              "$type": "QueryStatement"
            }
            """;

        Assert.Equal(new QueryModelLoadResult(new QueryStatement(Simple(Col("a"))), null), QueryModelJson.Deserialize(json));
    }

    private const string Body = """
        "body": { "$type": "Select", "distinct": false, "items": [ { "expr": { "$type": "ColumnRef", "name": "a" } } ], "from": [], "groupBy": [], "windows": [] }
        """;

    private const string Invalid = "The saved query does not match the query model: ";

    [Theory]
    [InlineData("null", "The saved query is empty.")]
    [InlineData("", "The saved query is empty.")]
    [InlineData("  ", "The saved query is empty.")]
    [InlineData("{", "The saved query is not valid JSON: ")]
    [InlineData("""{ "$type": "Nope" }""", Invalid)]
    [InlineData("{}", Invalid)]
    [InlineData("[]", Invalid)]
    [InlineData("""{ "$type": "QueryStatement" }""", Invalid)]
    [InlineData("""{ "$type": "QueryStatement", "query": { """ + Body + " } }", Invalid)]
    [InlineData("""{ "$type": "QueryStatement", "query": { """ + Body + """, "orderBy": null } }""", Invalid)]
    [InlineData("""{ "$type": "QueryStatement", "query": { """ + Body + """, "orderBy": [ null ] } }""", Invalid)]
    [InlineData("""{ "$type": "QueryStatement", "query": { "body": { "$type": "Select", "distinct": false, "items": [ { "expr": { "$type": "ColumnRef", "name": null } } ], "from": [], "groupBy": [], "windows": [] }, "orderBy": [] } }""", Invalid)]
    [InlineData("""{ "formatVersion": 2, "$type": "QueryStatement" }""", "The saved query was written by a newer version (format 2).")]
    [InlineData("""{ "formatVersion": "one", "$type": "QueryStatement" }""", "The saved query has an unreadable format version.")]
    public void Json_Deserialize_BadContent_ReturnsReadableErrorInsteadOfThrowing(string json, string expectedError)
    {
        var result = QueryModelJson.Deserialize(json);

        Assert.Null(result.Statement);
        Assert.StartsWith(expectedError, result.Error);
    }

    [Theory]
    [MemberData(nameof(EveryNode))]
    public void Json_RoundTrips_EveryNodeType(Statement model)
    {
        var read = QueryModelJson.Deserialize(QueryModelJson.Serialize(model));

        Assert.Equal(new QueryModelLoadResult(model, null), read);
    }
}
