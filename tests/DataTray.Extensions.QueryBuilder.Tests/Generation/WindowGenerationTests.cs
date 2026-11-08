using DataTray.Extensions.QueryBuilder.Model;
using static DataTray.Extensions.QueryBuilder.Tests.Generation.Build;

namespace DataTray.Extensions.QueryBuilder.Tests.Generation;

public class WindowGenerationTests
{
    private static readonly FunctionCall Sum = new("SUM", [Col("x")], false);

    private static WindowCall Over(Frame? frame) =>
        new(Sum, new WindowSpec(null, [Col("p")], [new OrderItem(Col("o"), true)], frame));

    private static Frame Frame(FrameUnit unit, FrameBound start, FrameBound? end, FrameExclude? exclude) =>
        new(unit, start, end, exclude);

    private static FrameBound Bound(BoundKind kind, long? offset) => new(kind, offset);

    public static TheoryData<string, string?, Expr, string> WindowCases() => new()
    {
        { "sqlserver", null, new WindowCall(new FunctionCall("ROW_NUMBER", [], false), new WindowSpec(null, [], [new OrderItem(Col("o"), false)], null)), "SELECT ROW_NUMBER() OVER (ORDER BY [o]);" },
        { "postgres", null, Over(null), "SELECT SUM(\"x\") OVER (PARTITION BY \"p\" ORDER BY \"o\" DESC);" },
        { "mysql", null, Over(Frame(FrameUnit.Rows, Bound(BoundKind.Preceding, 2), Bound(BoundKind.CurrentRow, null), null)), "SELECT SUM(`x`) OVER (PARTITION BY `p` ORDER BY `o` DESC ROWS BETWEEN 2 PRECEDING AND CURRENT ROW);" },
        { "sqlserver", null, Over(Frame(FrameUnit.Range, Bound(BoundKind.UnboundedPreceding, null), Bound(BoundKind.UnboundedFollowing, null), null)), "SELECT SUM([x]) OVER (PARTITION BY [p] ORDER BY [o] DESC RANGE BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING);" },
        { "sqlite", "3.28.0", Over(Frame(FrameUnit.Groups, Bound(BoundKind.CurrentRow, null), Bound(BoundKind.Following, 1), FrameExclude.Ties)), "SELECT SUM(\"x\") OVER (PARTITION BY \"p\" ORDER BY \"o\" DESC GROUPS BETWEEN CURRENT ROW AND 1 FOLLOWING EXCLUDE TIES);" },
        { "postgres", null, Over(Frame(FrameUnit.Range, Bound(BoundKind.Preceding, 5), null, FrameExclude.NoOthers)), "SELECT SUM(\"x\") OVER (PARTITION BY \"p\" ORDER BY \"o\" DESC RANGE 5 PRECEDING EXCLUDE NO OTHERS);" },
        { "postgres", null, Over(Frame(FrameUnit.Rows, Bound(BoundKind.UnboundedPreceding, null), null, FrameExclude.CurrentRow)), "SELECT SUM(\"x\") OVER (PARTITION BY \"p\" ORDER BY \"o\" DESC ROWS UNBOUNDED PRECEDING EXCLUDE CURRENT ROW);" },
        { "postgres", null, Over(Frame(FrameUnit.Rows, Bound(BoundKind.CurrentRow, null), null, FrameExclude.Group)), "SELECT SUM(\"x\") OVER (PARTITION BY \"p\" ORDER BY \"o\" DESC ROWS CURRENT ROW EXCLUDE GROUP);" },
        { "mysql", null, new WindowCall(Sum, new WindowSpec("w", [], [], null)), "SELECT SUM(`x`) OVER w;" },
        { "postgres", null, new WindowCall(Sum, new WindowSpec("w", [], [new OrderItem(Col("o"), false)], null)), "SELECT SUM(\"x\") OVER (w ORDER BY \"o\");" },
    };

    [Theory, MemberData(nameof(WindowCases))]
    public void Generate_WritesWindowCallsWithFrames(string provider, string? version, Expr expr, string expected)
    {
        Assert.Equal(expected, Generate(provider, version, expr).Sql);
    }

    public static TheoryData<string, string?, Expr, string> MissingCapabilities() => new()
    {
        { "sqlite", "3.24.0", Over(null), "window-functions" },
        { "sqlite", null, Over(null), "window-functions" },
        { "sqlserver", null, Over(Frame(FrameUnit.Groups, Bound(BoundKind.CurrentRow, null), null, null)), "groups-frame" },
        { "mysql", null, Over(Frame(FrameUnit.Groups, Bound(BoundKind.CurrentRow, null), null, null)), "groups-frame" },
        { "sqlite", "3.27.0", Over(Frame(FrameUnit.Range, Bound(BoundKind.Preceding, 1), null, null)), "range-offset" },
        { "sqlserver", null, Over(Frame(FrameUnit.Range, Bound(BoundKind.Preceding, 3), Bound(BoundKind.CurrentRow, null), null)), "range-offset" },
        { "sqlserver", null, Over(Frame(FrameUnit.Range, Bound(BoundKind.CurrentRow, null), Bound(BoundKind.Following, 3), null)), "range-offset" },
        { "mysql", null, Over(Frame(FrameUnit.Rows, Bound(BoundKind.CurrentRow, null), null, FrameExclude.Ties)), "frame-exclude" },
        { "sqlserver", "15.0.2000", new WindowCall(Sum, new WindowSpec("w", [], [], null)), "named-window" },
    };

    public static TheoryData<Frame, string> InvalidFrames() => new()
    {
        { Frame(FrameUnit.Rows, Bound(BoundKind.Following, 1), null, null), "invalid-frame" },
        { Frame(FrameUnit.Rows, Bound(BoundKind.UnboundedFollowing, null), Bound(BoundKind.UnboundedFollowing, null), null), "invalid-frame" },
        { Frame(FrameUnit.Rows, Bound(BoundKind.CurrentRow, null), Bound(BoundKind.Preceding, 1), null), "invalid-frame" },
        { Frame(FrameUnit.Rows, Bound(BoundKind.UnboundedPreceding, null), Bound(BoundKind.UnboundedPreceding, null), null), "invalid-frame" },
        { Frame(FrameUnit.Rows, Bound(BoundKind.Preceding, null), null, null), "invalid-frame-offset" },
    };

    [Theory, MemberData(nameof(InvalidFrames))]
    public void Generate_InvalidFrame_IsAProblem(Frame frame, string code)
    {
        var result = Generate("postgres", null, Over(frame));

        Assert.Null(result.Sql);
        Assert.Equal([code], Codes(result));
    }

    [Theory, MemberData(nameof(MissingCapabilities))]
    public void Generate_MissingWindowCapability_IsAProblem(string provider, string? version, Expr expr, string code)
    {
        var result = Generate(provider, version, expr);

        Assert.Null(result.Sql);
        Assert.Equal([code], Codes(result));
    }
}
