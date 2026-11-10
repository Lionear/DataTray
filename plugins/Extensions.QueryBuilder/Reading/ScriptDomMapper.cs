using System.Globalization;
using DataTray.Extensions.QueryBuilder.Generation;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using M = DataTray.Extensions.QueryBuilder.Model;

namespace DataTray.Extensions.QueryBuilder.Reading;

/// <summary>
/// Maps a ScriptDom SELECT to the model. ScriptDom is SQL Server's own grammar, so its tree is how the server reads the
/// text (the AstMapper rule holds by construction). A node the model has no place for becomes a Fragment or
/// FragmentSource holding the node's original text; a clause of a query itself throws Refused.
/// </summary>
internal sealed class ScriptDomMapper(string sql)
{
    // Their first argument is a datepart keyword; read as a column it would be written back as [day].
    private static readonly string[] DatePartFunctions = ["DATEADD", "DATEDIFF", "DATEDIFF_BIG", "DATENAME", "DATEPART", "DATETRUNC", "DATE_BUCKET"];

    private readonly List<string> _cteScope = [];
    private readonly List<string> _cteReferences = [];

    public int FragmentCount { get; private set; }

    public M.Query Statement(SelectStatement s)
    {
        if (s.Into is not null)
        {
            throw new Refused("SELECT INTO", s.Into.StartLine);
        }
        if (s.ComputeClauses is [var compute, ..])
        {
            throw new Refused("a COMPUTE clause", compute.StartLine);
        }
        if (s.OptimizerHints is [var hint, ..])
        {
            throw new Refused("an OPTION clause", hint.StartLine);
        }
        return Query(s.QueryExpression, s.WithCtesAndXmlNamespaces);
    }

    private M.Query Query(QueryExpression qe, WithCtesAndXmlNamespaces? with = null)
    {
        if (qe.ForClause is not null)
        {
            throw new Refused("a FOR XML, FOR JSON or FOR BROWSE clause", qe.ForClause.StartLine);
        }
        var scopeMark = _cteScope.Count;
        try
        {
            var w = with is null ? null : With(with);
            var body = Core(qe);
            var orderBy = qe.OrderByClause is null ? [] : OrderItems(qe.OrderByClause);
            return new M.Query(w, body, orderBy, Limit(qe is QuerySpecification s ? s.TopRowFilter : null, qe.OffsetClause, body));
        }
        finally
        {
            _cteScope.RemoveRange(scopeMark, _cteScope.Count - scopeMark);
        }
    }

    private M.With With(WithCtesAndXmlNamespaces with)
    {
        if (with.XmlNamespaces is not null || with.ChangeTrackingContext is not null)
        {
            throw new Refused("WITH XMLNAMESPACES or CHANGE_TRACKING_CONTEXT", with.StartLine);
        }
        var ctes = new List<M.Cte>();
        var recursive = false;
        foreach (var cte in with.CommonTableExpressions)
        {
            if (cte.WithCtesAndXmlNamespaces is not null)
            {
                throw new Refused("a WITH inside a common table expression", cte.StartLine);
            }
            // A T-SQL common table expression sees itself; referring to itself is what makes the WITH recursive.
            var name = cte.ExpressionName.Value;
            _cteScope.Add(name);
            var referenceMark = _cteReferences.Count;
            var columns = cte.Columns is { Count: > 0 } c ? new M.ValueList<string>(c.Select(i => i.Value)) : null;
            ctes.Add(new M.Cte(name, columns, Query(cte.QueryExpression)));
            recursive |= _cteReferences.Skip(referenceMark).Contains(name, StringComparer.OrdinalIgnoreCase);
        }
        return new M.With(recursive, ctes.ToArray());
    }

    private M.QueryBody Core(QueryExpression qe) => qe switch
    {
        QuerySpecification s => Select(s),
        BinaryQueryExpression b => new M.SetOperation(Body(b.FirstQueryExpression), SetOp(b.BinaryQueryExpressionType), b.All, Body(b.SecondQueryExpression)),
        QueryParenthesisExpression p => Body(p.QueryExpression),
        _ => throw new Refused("this kind of query body", qe.StartLine),
    };

    // Parentheses are only grouping, the model's tree carries that; an operand with its own ORDER BY, TOP or OFFSET is a nested query.
    private M.QueryBody Body(QueryExpression qe) =>
        qe.OrderByClause is null && qe.OffsetClause is null && qe.ForClause is null && qe is not QuerySpecification { TopRowFilter: not null }
            ? Core(qe)
            : new M.NestedQuery(Query(qe));

    private static M.SetOp SetOp(BinaryQueryExpressionType type) => type switch
    {
        BinaryQueryExpressionType.Union => M.SetOp.Union,
        BinaryQueryExpressionType.Intersect => M.SetOp.Intersect,
        _ => M.SetOp.Except,
    };

    private static M.RowLimit? Limit(TopRowFilter? top, OffsetClause? offset, M.QueryBody body)
    {
        if (top is not null)
        {
            if (top.Percent || top.WithTies || top.WithApproximate)
            {
                throw new Refused("TOP with PERCENT or WITH TIES", top.StartLine);
            }
            return new M.RowLimit(Count(top.Expression), null);
        }
        if (offset is null)
        {
            return null;
        }
        if (offset.FetchExpression is null || offset.WithApproximate)
        {
            throw new Refused("an OFFSET without a row count", offset.StartLine);
        }
        // A plain SELECT without offset is written with TOP, so OFFSET 0 there means offset 0; elsewhere it means none.
        var skip = Count(offset.OffsetExpression);
        return new M.RowLimit(Count(offset.FetchExpression), body is M.Select || skip != 0 ? skip : null);
    }

    private static long Count(ScalarExpression e) => e switch
    {
        ParenthesisExpression p => Count(p.Expression),
        IntegerLiteral i when long.TryParse(i.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) => count,
        _ => throw new Refused("a row count that is not a whole number", e.StartLine),
    };

    private M.Select Select(QuerySpecification s)
    {
        if (s.WhereClause is { Cursor: not null })
        {
            throw new Refused("WHERE CURRENT OF", s.WhereClause.StartLine);
        }
        M.Expr[] groupBy = s.GroupByClause switch
        {
            null => [],
            { All: false, GroupByOption: GroupByOption.None } g when g.GroupingSpecifications.All(x => x is ExpressionGroupingSpecification { DistributedAggregation: false }) =>
                g.GroupingSpecifications.Cast<ExpressionGroupingSpecification>().Select(x => Expr(x.Expression)).ToArray(),
            var g => throw new Refused("GROUP BY ALL, ROLLUP, CUBE or GROUPING SETS", g.StartLine),
        };
        return new M.Select(
            s.UniqueRowFilter == UniqueRowFilter.Distinct,
            s.SelectElements.Select(SelectItem).ToArray(),
            (s.FromClause?.TableReferences ?? []).Select(Source).ToArray(),
            s.WhereClause is null ? null : Expr(s.WhereClause.SearchCondition),
            groupBy,
            s.HavingClause is null ? null : Expr(s.HavingClause.SearchCondition),
            (s.WindowClause?.WindowDefinition ?? []).Select(NamedWindow).ToArray());
    }

    // "alias = expr" and "expr AS alias" are the same select item to the server.
    private M.SelectItem SelectItem(SelectElement e) => e switch
    {
        SelectScalarExpression s => new M.SelectItem(Expr(s.Expression), s.ColumnName?.Value),
        SelectStarExpression { Qualifier: null } => new M.SelectItem(new M.Star(null), null),
        SelectStarExpression { Qualifier.Identifiers: [var q] } => new M.SelectItem(new M.Star(q.Value), null),
        _ => new M.SelectItem(Fragment(e), null),
    };

    private M.NamedWindow NamedWindow(WindowDefinition w) =>
        new(w.WindowName.Value, WindowSpec(w.RefWindowName, w.Partitions, w.OrderByClause, w.WindowFrameClause));

    private M.WindowSpec WindowSpec(Identifier? baseWindow, IList<ScalarExpression>? partitions, OrderByClause? orderBy, WindowFrameClause? frame) => new(
        baseWindow?.Value,
        (partitions ?? []).Select(Expr).ToArray(),
        orderBy is null ? [] : OrderItems(orderBy),
        frame is null ? null : Frame(frame));

    private M.ValueList<M.OrderItem> OrderItems(OrderByClause orderBy) =>
        orderBy.OrderByElements.Select(o => new M.OrderItem(Expr(o.Expression), o.SortOrder == SortOrder.Descending)).ToArray();

    private static M.Frame Frame(WindowFrameClause frame) => new(
        frame.WindowFrameType == WindowFrameType.Rows ? M.FrameUnit.Rows : M.FrameUnit.Range,
        Bound(frame.Top),
        frame.Bottom is null ? null : Bound(frame.Bottom),
        null);

    private static M.FrameBound Bound(WindowDelimiter bound) => bound.WindowDelimiterType switch
    {
        WindowDelimiterType.CurrentRow => new M.FrameBound(M.BoundKind.CurrentRow, null),
        WindowDelimiterType.UnboundedPreceding => new M.FrameBound(M.BoundKind.UnboundedPreceding, null),
        WindowDelimiterType.UnboundedFollowing => new M.FrameBound(M.BoundKind.UnboundedFollowing, null),
        WindowDelimiterType.ValuePreceding => new M.FrameBound(M.BoundKind.Preceding, FrameOffset(bound.OffsetValue)),
        _ => new M.FrameBound(M.BoundKind.Following, FrameOffset(bound.OffsetValue)),
    };

    private static long FrameOffset(ScalarExpression e) =>
        e is IntegerLiteral i && long.TryParse(i.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
            ? offset
            : throw new Unrepresentable("a frame offset");

    // Sources

    private M.Source Source(TableReference reference)
    {
        var mark = FragmentCount;
        try
        {
            return Reference(reference);
        }
        catch (Unrepresentable)
        {
            FragmentCount = mark;
            return FragmentSource(reference);
        }
    }

    private M.Source Reference(TableReference reference) => reference switch
    {
        QualifiedJoin { JoinHint: JoinHint.None } j => new M.JoinSource(Reference(j.FirstTableReference), JoinType(j.QualifiedJoinType), Reference(j.SecondTableReference), Expr(j.SearchCondition)),
        QualifiedJoin => throw new Unrepresentable("a join hint"),
        UnqualifiedJoin j => new M.JoinSource(Reference(j.FirstTableReference), JoinType(j.UnqualifiedJoinType), Reference(j.SecondTableReference), null),
        JoinParenthesisTableReference p => Reference(p.Join),
        NamedTableReference t => Table(t),
        QueryDerivedTable { Columns.Count: 0, Alias: { } alias, ForPath: false } d => new M.SubquerySource(Query(d.QueryExpression), alias.Value, false),
        // A single table, function or derived table is one unit of the FROM clause, so its own text can stand in for it.
        _ => FragmentSource(reference),
    };

    private static M.JoinType JoinType(QualifiedJoinType type) => type switch
    {
        QualifiedJoinType.Inner => M.JoinType.Inner,
        QualifiedJoinType.LeftOuter => M.JoinType.Left,
        QualifiedJoinType.RightOuter => M.JoinType.Right,
        _ => M.JoinType.Full,
    };

    private static M.JoinType JoinType(UnqualifiedJoinType type) => type switch
    {
        UnqualifiedJoinType.CrossJoin => M.JoinType.Cross,
        UnqualifiedJoinType.CrossApply => M.JoinType.CrossApply,
        UnqualifiedJoinType.OuterApply => M.JoinType.OuterApply,
        _ => throw new Unrepresentable("this join"),
    };

    private M.Source Table(NamedTableReference t)
    {
        var name = t.SchemaObject;
        if (t.TableHints.Count > 0 || t.TableSampleClause is not null || t.TemporalClause is not null || t.ForPath
            || name.ServerIdentifier is not null || name.DatabaseIdentifier is not null)
        {
            return FragmentSource(t);
        }
        var alias = t.Alias?.Value;
        var baseName = name.BaseIdentifier.Value;
        if (name.SchemaIdentifier is null && _cteScope.Contains(baseName, StringComparer.OrdinalIgnoreCase))
        {
            _cteReferences.Add(baseName);
            return new M.CteSource(baseName, alias);
        }
        return new M.TableSource(new M.TableName(name.SchemaIdentifier?.Value, baseName), alias);
    }

    // Expressions

    private M.Expr Expr(TSqlFragment e)
    {
        var mark = FragmentCount;
        try
        {
            return Map(e);
        }
        catch (Unrepresentable)
        {
            FragmentCount = mark;
            return Fragment(e);
        }
    }

    private M.Expr Map(TSqlFragment e) => e switch
    {
        // ScriptDom's offsets for a MATCH predicate leave out "MATCH(", so it can't even be kept as text.
        GraphMatchPredicate g => throw new Refused("a graph MATCH predicate", g.StartLine),
        PrimaryExpression { Collation: not null } => throw new Unrepresentable("COLLATE"),
        ColumnReferenceExpression { ColumnType: ColumnType.Regular, MultiPartIdentifier.Identifiers: [var n] } => new M.ColumnRef(null, n.Value),
        ColumnReferenceExpression { ColumnType: ColumnType.Regular, MultiPartIdentifier.Identifiers: [var q, var n] } => new M.ColumnRef(q.Value, n.Value),
        ColumnReferenceExpression { ColumnType: ColumnType.Wildcard, MultiPartIdentifier: null } => new M.Star(null),
        ColumnReferenceExpression { ColumnType: ColumnType.Wildcard, MultiPartIdentifier.Identifiers: [var q] } => new M.Star(q.Value),
        // Only number text the generator writes back unchanged; ScriptDom also takes "1e" as a real.
        (IntegerLiteral or NumericLiteral or RealLiteral) and Literal { Value: var number } when SqlGenerator.NumberText().IsMatch(number) =>
            new M.Literal(M.LiteralKind.Number, number),
        StringLiteral s => new M.Literal(s.IsNational ? M.LiteralKind.String : M.LiteralKind.NarrowString, s.Value),
        NullLiteral => new M.Literal(M.LiteralKind.Null, "NULL"),
        ParenthesisExpression p => Map(p.Expression),
        BooleanParenthesisExpression p => Map(p.Expression),
        UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative, Expression: (IntegerLiteral or NumericLiteral or RealLiteral) and Literal { Collation: null } n } when SqlGenerator.NumberText().IsMatch(n.Value) =>
            new M.Literal(M.LiteralKind.Number, "-" + n.Value),
        UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative } u => new M.Unary(M.UnaryOp.Negate, Expr(u.Expression)),
        // T-SQL + adds or concatenates depending on the operand types; Add is written back as the same +.
        BinaryExpression b => new M.Binary(BinaryOp(b.BinaryExpressionType), Expr(b.FirstExpression), Expr(b.SecondExpression)),
        BooleanNotExpression { Expression: ExistsPredicate x } => new M.Exists(Query(x.Subquery.QueryExpression), true),
        BooleanNotExpression n => new M.Unary(M.UnaryOp.Not, Expr(n.Expression)),
        BooleanBinaryExpression b => new M.Binary(b.BinaryExpressionType == BooleanBinaryExpressionType.And ? M.BinaryOp.And : M.BinaryOp.Or, Expr(b.FirstExpression), Expr(b.SecondExpression)),
        // Without booleans the generator writes TRUE/FALSE in predicate position as 1 = 1 / 1 = 0.
        BooleanComparisonExpression { ComparisonType: BooleanComparisonType.Equals, FirstExpression: IntegerLiteral { Value: "1", Collation: null }, SecondExpression: IntegerLiteral { Value: "1" or "0", Collation: null } right } =>
            new M.Literal(M.LiteralKind.Boolean, right.Value == "1" ? "true" : "false"),
        BooleanComparisonExpression c => new M.Binary(Comparison(c.ComparisonType), Expr(c.FirstExpression), Expr(c.SecondExpression)),
        BooleanIsNullExpression i => new M.IsNull(Expr(i.Expression), i.IsNot),
        BooleanTernaryExpression t => new M.Between(Expr(t.FirstExpression), Expr(t.SecondExpression), Expr(t.ThirdExpression), t.TernaryExpressionType == BooleanTernaryExpressionType.NotBetween),
        LikePredicate { EscapeExpression: null, OdbcEscape: false } l => Like(l.FirstExpression, l.SecondExpression, l.NotDefined),
        InPredicate { Subquery: { } s } i => new M.InSubquery(Expr(i.Expression), Query(s.QueryExpression), i.NotDefined),
        InPredicate i => new M.InList(Expr(i.Expression), i.Values.Select(Expr).ToArray(), i.NotDefined),
        ExistsPredicate x => new M.Exists(Query(x.Subquery.QueryExpression), false),
        SubqueryComparisonPredicate { SubqueryComparisonPredicateType: SubqueryComparisonPredicateType.Any or SubqueryComparisonPredicateType.All } q => new M.Quantified(
            Expr(q.Expression),
            Comparison(q.ComparisonType),
            q.SubqueryComparisonPredicateType == SubqueryComparisonPredicateType.Any ? M.Quantifier.Any : M.Quantifier.All,
            Query(q.Subquery.QueryExpression)),
        ScalarSubquery s => new M.ScalarSubquery(Query(s.QueryExpression)),
        SearchedCaseExpression c => new M.Case(
            null,
            c.WhenClauses.Select(w => new M.CaseWhen(Expr(w.WhenExpression), Expr(w.ThenExpression))).ToArray(),
            c.ElseExpression is null ? null : Expr(c.ElseExpression)),
        SimpleCaseExpression c => new M.Case(
            Expr(c.InputExpression),
            c.WhenClauses.Select(w => new M.CaseWhen(Expr(w.WhenExpression), Expr(w.ThenExpression))).ToArray(),
            c.ElseExpression is null ? null : Expr(c.ElseExpression)),
        CastCall c => new M.Cast(Expr(c.Parameter), Text(c.DataType)),
        CoalesceExpression c => Call(c, c.Expressions),
        NullIfExpression n => Call(n, [n.FirstExpression, n.SecondExpression]),
        LeftFunctionCall l => Call(l, l.Parameters),
        RightFunctionCall r => Call(r, r.Parameters),
        FunctionCall f => Function(f),
        _ => throw new Unrepresentable("this expression"),
    };

    // COALESCE, NULLIF, LEFT and RIGHT have their own nodes; the name keeps the spelling of its token.
    private M.FunctionCall Call(TSqlFragment node, IEnumerable<ScalarExpression> args) =>
        new(node.ScriptTokenStream[node.FirstTokenIndex].Text, args.Select(Expr).ToArray(), false);

    private M.Expr Function(FunctionCall f)
    {
        var name = f.FunctionName.Value;
        if (f.CallTarget is not null || f.FunctionName.QuoteType != QuoteType.NotQuoted || f.UniqueRowFilter == UniqueRowFilter.All
            || f.WithinGroupClause is not null || f.IgnoreRespectNulls is { Count: > 0 } || f.TrimOptions is not null
            || f.JsonParameters is { Count: > 0 } || f.JsonOrderByClause is not null || f.AbsentOrNullOnNull is { Count: > 0 }
            || f.ReturnType is { Count: > 0 } || f.WithArrayWrapper || DatePartFunctions.Contains(name, StringComparer.OrdinalIgnoreCase)
            || !PlainArguments(f))
        {
            throw new Unrepresentable("this function call");
        }
        var args = f.Parameters.Select(Expr).ToArray();
        var distinct = f.UniqueRowFilter == UniqueRowFilter.Distinct;
        if (f.OverClause is null && !distinct && args.Length >= 2 && name.Equals("CONCAT", StringComparison.OrdinalIgnoreCase))
        {
            return new M.Concat(args);
        }
        var call = new M.FunctionCall(name, args, distinct);
        return f.OverClause is { } over
            ? new M.WindowCall(call, WindowSpec(over.WindowName, over.Partitions, over.OrderByClause, over.WindowFrameClause))
            : call;
    }

    // ScriptDom folds keyword syntax such as TRIM(x FROM y) into Parameters; any token between the parentheses that is
    // not part of a parameter, a comma, DISTINCT/ALL or trivia means the call is not a plain argument list.
    private static bool PlainArguments(FunctionCall f)
    {
        var tokens = f.ScriptTokenStream;
        var i = f.FunctionName.LastTokenIndex + 1;
        while (tokens[i].TokenType != TSqlTokenType.LeftParenthesis)
        {
            i++;
        }
        i++;
        foreach (var parameter in f.Parameters)
        {
            for (; i < parameter.FirstTokenIndex; i++)
            {
                if (!Separator(tokens[i].TokenType))
                {
                    return false;
                }
            }
            i = parameter.LastTokenIndex + 1;
        }
        for (; tokens[i].TokenType != TSqlTokenType.RightParenthesis; i++)
        {
            if (!Separator(tokens[i].TokenType))
            {
                return false;
            }
        }
        return true;
    }

    private static bool Separator(TSqlTokenType type) => type is TSqlTokenType.Comma or TSqlTokenType.WhiteSpace
        or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment or TSqlTokenType.Distinct or TSqlTokenType.All;

    // LOWER(x) LIKE LOWER(p) is how the generator writes a case-insensitive LIKE (SQL Server has no ILIKE).
    private M.Expr Like(ScalarExpression value, ScalarExpression pattern, bool not) =>
        LowerArgument(value) is { } v && LowerArgument(pattern) is { } p
            ? new M.Like(Expr(v), Expr(p), true, not)
            : new M.Like(Expr(value), Expr(pattern), false, not);

    private static ScalarExpression? LowerArgument(ScalarExpression e) =>
        e is FunctionCall { CallTarget: null, OverClause: null, Collation: null, UniqueRowFilter: UniqueRowFilter.NotSpecified, FunctionName: { QuoteType: QuoteType.NotQuoted } name, Parameters: [var arg] }
            && name.Value.Equals("LOWER", StringComparison.OrdinalIgnoreCase)
            ? arg
            : null;

    private static M.BinaryOp Comparison(BooleanComparisonType type) => type switch
    {
        BooleanComparisonType.Equals => M.BinaryOp.Eq,
        BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation => M.BinaryOp.NotEq,
        BooleanComparisonType.LessThan => M.BinaryOp.Lt,
        BooleanComparisonType.LessThanOrEqualTo => M.BinaryOp.LtEq,
        BooleanComparisonType.GreaterThan => M.BinaryOp.Gt,
        BooleanComparisonType.GreaterThanOrEqualTo => M.BinaryOp.GtEq,
        _ => throw new Unrepresentable("this comparison"),
    };

    private static M.BinaryOp BinaryOp(BinaryExpressionType type) => type switch
    {
        BinaryExpressionType.Add => M.BinaryOp.Add,
        BinaryExpressionType.Subtract => M.BinaryOp.Subtract,
        BinaryExpressionType.Multiply => M.BinaryOp.Multiply,
        BinaryExpressionType.Divide => M.BinaryOp.Divide,
        BinaryExpressionType.Modulo => M.BinaryOp.Modulo,
        _ => throw new Unrepresentable("this operator"),
    };

    private string Text(TSqlFragment node) => sql.Substring(node.StartOffset, node.FragmentLength);

    private M.Fragment Fragment(TSqlFragment node)
    {
        FragmentCount++;
        return new M.Fragment(Text(node));
    }

    private M.FragmentSource FragmentSource(TSqlFragment node)
    {
        FragmentCount++;
        return new M.FragmentSource(Text(node));
    }
}
