using System.Text.RegularExpressions;
using DataTray.Extensions.QueryBuilder.Dialects;
using DataTray.Extensions.QueryBuilder.Model;

namespace DataTray.Extensions.QueryBuilder.Generation;

/// <summary>
/// Writes a statement for one dialect. A node the dialect cannot express becomes a problem, never a rewrite;
/// all problems are collected in one pass and any problem means no SQL.
/// </summary>
public sealed partial class SqlGenerator
{
    private readonly IDialect _dialect;
    private readonly DialectCapabilities _can;
    private readonly SqlWriter _w = new();
    private readonly List<GenerationProblem> _problems = [];

    private SqlGenerator(IDialect dialect)
    {
        _dialect = dialect;
        _can = dialect.Capabilities;
    }

    public static GenerationResult Generate(Statement statement, IDialect dialect)
    {
        if (statement is not QueryStatement query)
        {
            return new GenerationResult(null, [new GenerationProblem("dml-not-yet", "Data-changing statements are not generated yet.", statement)]);
        }

        var generator = new SqlGenerator(dialect);
        generator.WriteQuery(query.Query, false);
        generator._w.Write(";");
        return generator._problems.Count > 0
            ? new GenerationResult(null, generator._problems)
            : new GenerationResult(generator._w.ToString(), []);
    }

    // [0-9], not \d: \d also matches non-ASCII digits.
    [GeneratedRegex(@"^-?([0-9]+(\.[0-9]*)?|\.[0-9]+)([eE][+-]?[0-9]+)?\z")]
    private static partial Regex NumberText();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*\z")]
    private static partial Regex SimpleName();

    [GeneratedRegex(@"^[a-z_][a-z0-9_]*\z")]
    private static partial Regex LowerCaseName();

    // Nested = inside parentheses (subquery, derived table, CTE, set-operation branch); inSubquery = the
    // query of an IN/ANY/ALL predicate, where MySQL refuses a limit.
    private void WriteQuery(Query query, bool nested, bool inSubquery = false)
    {
        if (query.With is { } with)
        {
            if (nested && !_can.NestedWith)
            {
                Problem("nested-with", $"{_dialect.Id} does not allow WITH inside a subquery.", query);
            }

            WriteWith(with);
        }

        if (nested && _can.OrderByInSubqueryNeedsLimit && query.OrderBy.Count > 0 && query.Limit is null)
        {
            Problem("order-by-needs-limit", $"{_dialect.Id} only allows ORDER BY in a subquery or CTE together with a row limit.", query);
        }

        if (inSubquery && !_can.LimitInInSubquery && query.Limit is not null)
        {
            Problem("limit-in-in-subquery", $"{_dialect.Id} does not allow a row limit in an IN, ANY or ALL subquery.", query);
        }

        if (query.Limit is { } limit && (limit.Count < 0 || limit.Offset < 0))
        {
            Problem("invalid-limit", "A row limit and offset cannot be negative.", limit);
        }

        // SQL Server: TOP only on a plain SELECT without offset; everything else takes OFFSET/FETCH.
        var top = _can.LimitStyle == LimitStyle.Top && query.Body is Select && query.Limit is { Offset: null } ? query.Limit : null;
        WriteBody(query.Body, top);
        if (query.OrderBy.Count > 0)
        {
            _w.NewLine();
            WriteOrderBy(query.OrderBy);
        }

        if (query.Limit is not { } rowLimit || top is not null)
        {
            return;
        }

        _w.NewLine();
        if (_can.LimitStyle == LimitStyle.Top)
        {
            if (query.OrderBy.Count == 0)
            {
                Problem("offset-needs-order-by", $"{_dialect.Id} needs an ORDER BY for OFFSET/FETCH.", query);
            }

            _w.Write($"OFFSET {rowLimit.Offset ?? 0} ROWS FETCH NEXT {rowLimit.Count} ROWS ONLY");
        }
        else
        {
            _w.Write($"LIMIT {rowLimit.Count}" + (rowLimit.Offset is { } offset ? $" OFFSET {offset}" : string.Empty));
        }
    }

    private void WriteWith(With with)
    {
        _w.Write(with.Recursive && _can.RecursiveKeyword ? "WITH RECURSIVE " : "WITH ");
        for (var i = 0; i < with.Ctes.Count; i++)
        {
            var cte = with.Ctes[i];
            if (i > 0)
            {
                _w.Write(",");
                _w.NewLine();
            }

            _w.Write(_dialect.QuoteIdentifier(cte.Name));
            if (cte.Columns is { } columns)
            {
                _w.Write(" (" + string.Join(", ", columns.Select(_dialect.QuoteIdentifier)) + ")");
            }

            _w.Write(" AS ");
            _w.Nested(() => WriteQuery(cte.Query, true));
        }

        _w.NewLine();
    }

    private void WriteBody(QueryBody body, RowLimit? top)
    {
        switch (body)
        {
            case Select select:
                WriteSelect(select, top);
                break;
            case SetOperation op:
                WriteSetOperation(op);
                break;
            case ValuesBody values:
                WriteValues(values);
                break;
            case NestedQuery nested:
                WriteParenthesized(() => WriteQuery(nested.Query, true), nested);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(body), body, null);
        }
    }

    // Left-deep chains print flat. A right operand that is itself a set operation is parenthesized, and so is a
    // UNION/EXCEPT left of an INTERSECT where INTERSECT binds tighter.
    private void WriteSetOperation(SetOperation op)
    {
        if (op.Op != SetOp.Union && !_can.IntersectExcept)
        {
            Problem("intersect-except", $"{_dialect.Id} does not support INTERSECT or EXCEPT (at this server version).", op);
        }
        else if (op.Op != SetOp.Union && op.All && !_can.IntersectExceptAll)
        {
            Problem("intersect-except-all", $"{_dialect.Id} does not support INTERSECT ALL or EXCEPT ALL (at this server version).", op);
        }

        var wrapLeft = _can.IntersectBindsTighter && op.Op == SetOp.Intersect && op.Left is SetOperation { Op: not SetOp.Intersect };
        WriteOperand(op.Left, wrapLeft);
        _w.NewLine();
        _w.Write(op.Op.ToString().ToUpperInvariant() + (op.All ? " ALL" : string.Empty));
        _w.NewLine();
        WriteOperand(op.Right, op.Right is SetOperation);
    }

    private void WriteOperand(QueryBody body, bool wrap)
    {
        if (wrap)
        {
            WriteParenthesized(() => WriteBody(body, null), body);
        }
        else
        {
            WriteBody(body, null);
        }
    }

    private void WriteParenthesized(Action body, QueryBody node)
    {
        if (!_can.ParenthesizedSetOperands)
        {
            Problem("parenthesized-query", $"{_dialect.Id} cannot put a query in parentheses here.", node);
        }

        _w.Nested(body);
    }

    private void WriteValues(ValuesBody values)
    {
        if (_can.Values == ValuesStyle.None)
        {
            Problem("values", $"{_dialect.Id} does not support a VALUES query (at this server version).", values);
        }

        const string head = "VALUES ";
        _w.Write(head);
        for (var i = 0; i < values.Rows.Count; i++)
        {
            if (i > 0)
            {
                _w.Write(",");
                _w.NewLine(head.Length);
            }

            _w.Write(_can.Values == ValuesStyle.Row ? "ROW(" : "(");
            WriteList(values.Rows[i]);
            _w.Write(")");
        }
    }

    private void WriteSelect(Select select, RowLimit? top)
    {
        var head = (select.Distinct ? "SELECT DISTINCT " : "SELECT ") + (top is null ? string.Empty : $"TOP ({top.Count}) ");
        _w.Write(head);
        for (var i = 0; i < select.Items.Count; i++)
        {
            if (i > 0)
            {
                _w.Write(",");
                _w.NewLine(head.Length);
            }

            WriteExpr(select.Items[i].Expr, 0);
            if (select.Items[i].Alias is { } alias)
            {
                _w.Write(" AS " + _dialect.QuoteIdentifier(alias));
            }
        }

        if (select.From.Count > 0)
        {
            _w.NewLine();
            _w.Write("FROM ");
            for (var i = 0; i < select.From.Count; i++)
            {
                if (i > 0)
                {
                    _w.Write(",");
                    _w.NewLine("FROM ".Length);
                }

                WriteSource(select.From[i], false);
            }
        }

        if (select.Where is not null)
        {
            _w.NewLine();
            _w.Write("WHERE ");
            WriteExpr(select.Where, 0, true);
        }

        if (select.GroupBy.Count > 0)
        {
            _w.NewLine();
            _w.Write("GROUP BY ");
            WriteList(select.GroupBy);
        }

        if (select.Having is not null)
        {
            _w.NewLine();
            _w.Write("HAVING ");
            WriteExpr(select.Having, 0, true);
        }

        if (select.Windows.Count > 0)
        {
            _w.NewLine();
            _w.Write("WINDOW ");
            for (var i = 0; i < select.Windows.Count; i++)
            {
                Require(_can.NamedWindow, "named-window", "named windows", select.Windows[i]);
                _w.Write((i > 0 ? ", " : string.Empty) + Alias(select.Windows[i].Name) + " AS ");
                WriteWindowSpec(select.Windows[i].Spec);
            }
        }
    }

    private void WriteSource(Source source, bool inline)
    {
        switch (source)
        {
            case TableSource t:
                _w.Write(TableName(t.Name) + AliasClause(t.Alias));
                break;
            case CteSource c:
                _w.Write(_dialect.QuoteIdentifier(c.Name) + AliasClause(c.Alias));
                break;
            case FragmentSource f:
                _w.Write(f.Sql);
                break;
            case SubquerySource s:
                if (s.Lateral)
                {
                    Require(_can.Lateral, "lateral", "LATERAL", s);
                    _w.Write("LATERAL ");
                }

                _w.Nested(() => WriteQuery(s.Query, true));
                _w.Write(AliasClause(s.Alias));
                break;
            case JoinSource j:
                WriteJoin(j, inline);
                break;
        }
    }

    private void WriteJoin(JoinSource join, bool inline)
    {
        var takesOn = join.Type is JoinType.Inner or JoinType.Left or JoinType.Right or JoinType.Full;
        if (takesOn && join.On is null)
        {
            Problem("join-needs-on", $"{join.Type} join needs an ON condition.", join);
        }
        else if (!takesOn && join.On is not null)
        {
            Problem("join-unexpected-on", $"{join.Type} join takes no ON condition.", join);
        }

        WriteSource(join.Left, inline);
        if (inline)
        {
            _w.Write(" ");
        }
        else
        {
            _w.NewLine();
        }

        _w.Write(JoinKeyword(join) + " ");
        if (join.Right is JoinSource nested)
        {
            _w.Write("(");
            WriteJoin(nested, true);
            _w.Write(")");
        }
        else
        {
            WriteSource(join.Right, inline);
        }

        if (join.On is not null)
        {
            _w.Write(" ON ");
            WriteExpr(join.On, 0, true);
        }
    }

    private string JoinKeyword(JoinSource join)
    {
        switch (join.Type)
        {
            case JoinType.Right:
                Require(_can.RightJoin, "right-join", "RIGHT JOIN", join);
                return "RIGHT JOIN";
            case JoinType.Full:
                Require(_can.FullJoin, "full-join", "FULL JOIN", join);
                return "FULL JOIN";
            case JoinType.CrossApply:
                Require(_can.Apply, "apply", "CROSS APPLY", join);
                return "CROSS APPLY";
            case JoinType.OuterApply:
                Require(_can.Apply, "apply", "OUTER APPLY", join);
                return "OUTER APPLY";
            case JoinType.Left:
                return "LEFT JOIN";
            case JoinType.Cross:
                return "CROSS JOIN";
            case JoinType.Inner:
                return "INNER JOIN";
            default:
                throw new ArgumentOutOfRangeException(nameof(join), join.Type, null);
        }
    }

    // Writes expr, wrapped in parentheses when it binds looser than the parent requires. Without boolean
    // literals a condition only exists in predicate position: a bare TRUE/FALSE there becomes `1 = 1`/`1 = 0`,
    // and a condition in value position is refused (`SELECT [a] = 1` would be an alias assignment).
    private void WriteExpr(Expr expr, int required, bool predicate = false)
    {
        if (!_can.BooleanLiterals && predicate && expr is Literal { Kind: LiteralKind.Boolean } literal)
        {
            WriteBooleanPredicate(literal, required);
            return;
        }

        if (!_can.BooleanLiterals && !predicate && IsCondition(expr))
        {
            Problem("predicate-as-value", $"{_dialect.Id} has no boolean values; a condition cannot be used as a value here.", expr);
        }

        var wrap = Precedence.Of(expr, _can.ConcatStyle) < required;
        if (wrap)
        {
            _w.Write("(");
        }

        WriteBare(expr);
        if (wrap)
        {
            _w.Write(")");
        }
    }

    private void WriteBooleanPredicate(Literal literal, int required)
    {
        var wrap = Precedence.Comparison < required;
        _w.Write(wrap ? "(" : string.Empty);
        _w.Write("1 = ");
        WriteLiteral(literal);
        _w.Write(wrap ? ")" : string.Empty);
    }

    private static bool IsCondition(Expr expr) => expr switch
    {
        Binary b => Precedence.Of(b.Op) <= Precedence.Comparison,
        Unary u => u.Op == UnaryOp.Not,
        Between or Like or IsNull or InList or InSubquery or Exists or Quantified => true,
        _ => false,
    };

    private void WriteBare(Expr expr)
    {
        const int operand = Precedence.Comparison + 1;
        switch (expr)
        {
            case ColumnRef c:
                _w.Write((c.Qualifier is null ? string.Empty : Alias(c.Qualifier) + ".") + _dialect.QuoteIdentifier(c.Name));
                break;
            case Star s:
                _w.Write(s.Qualifier is null ? "*" : Alias(s.Qualifier) + ".*");
                break;
            case Literal l:
                WriteLiteral(l);
                break;
            case Fragment f:
                _w.Write(f.Sql);
                break;
            case Unary { Op: UnaryOp.Not } u:
                _w.Write("NOT ");
                WriteExpr(u.Operand, Precedence.Not, true);
                break;
            case Unary { Op: UnaryOp.Negate } u:
                _w.Write("-");
                WriteExpr(u.Operand, Precedence.Primary);
                break;
            case Binary b:
                WriteBinary(b);
                break;
            case Between b:
                WriteExpr(b.Value, operand);
                _w.Write(b.Not ? " NOT BETWEEN " : " BETWEEN ");
                WriteExpr(b.Low, operand);
                _w.Write(" AND ");
                WriteExpr(b.High, operand);
                break;
            case Like l:
                WriteLike(l);
                break;
            case IsNull n:
                WriteExpr(n.Value, operand);
                _w.Write(n.Not ? " IS NOT NULL" : " IS NULL");
                break;
            case InList i:
                WriteExpr(i.Value, operand);
                _w.Write(i.Not ? " NOT IN (" : " IN (");
                WriteList(i.Items);
                _w.Write(")");
                break;
            case InSubquery i:
                WriteExpr(i.Value, operand);
                _w.Write(i.Not ? " NOT IN " : " IN ");
                _w.Nested(() => WriteQuery(i.Query, true, true));
                break;
            case Exists e:
                _w.Write(e.Not ? "NOT EXISTS " : "EXISTS ");
                _w.Nested(() => WriteQuery(e.Query, true));
                break;
            case Quantified q:
                Require(_can.QuantifiedSubquery, "quantified-subquery", "ANY/ALL subqueries", q);
                if (Precedence.Of(q.Comparison) != Precedence.Comparison)
                {
                    Problem("invalid-quantified-op", $"ANY/ALL needs a comparison, not {q.Comparison}.", q);
                }

                WriteExpr(q.Value, operand);
                _w.Write($" {Operator(q.Comparison)} {(q.Quantifier == Quantifier.All ? "ALL" : "ANY")} ");
                _w.Nested(() => WriteQuery(q.Query, true, true));
                break;
            case ScalarSubquery s:
                _w.Nested(() => WriteQuery(s.Query, true));
                break;
            case FunctionCall f:
                WriteCall(f);
                break;
            case WindowCall w:
                WriteWindowCall(w);
                break;
            case Case c:
                WriteCase(c);
                break;
            case Cast c:
                _w.Write("CAST(");
                WriteExpr(c.Value, 0);
                _w.Write(" AS " + c.TypeName + ")");
                break;
            case Concat c:
                WriteConcat(c);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(expr), expr, null);
        }
    }

    private void WriteBinary(Binary b)
    {
        var level = Precedence.Of(b.Op);
        var logical = b.Op is BinaryOp.And or BinaryOp.Or;
        WriteExpr(b.Left, level == Precedence.Comparison ? level + 1 : level, logical);
        _w.Write($" {Operator(b.Op)} ");
        WriteExpr(b.Right, level + 1, logical);
    }

    private void WriteLiteral(Literal literal)
    {
        switch (literal.Kind)
        {
            case LiteralKind.String:
                _w.Write(_dialect.StringLiteral(literal.Text));
                break;
            case LiteralKind.Number:
                if (!NumberText().IsMatch(literal.Text))
                {
                    Problem("invalid-number", $"'{literal.Text}' is not a number.", literal);
                }

                _w.Write(literal.Text);
                break;
            case LiteralKind.Boolean:
                if (!bool.TryParse(literal.Text, out var value))
                {
                    Problem("invalid-boolean", $"'{literal.Text}' is not a boolean.", literal);
                }

                _w.Write(_dialect.BooleanLiteral(value));
                break;
            case LiteralKind.Null:
                _w.Write("NULL");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(literal), literal.Kind, null);
        }
    }

    private void WriteLike(Like like)
    {
        const int operand = Precedence.Comparison + 1;
        var not = like.Not ? " NOT" : string.Empty;
        if (!like.CaseInsensitive)
        {
            WriteExpr(like.Value, operand);
            _w.Write(not + " LIKE ");
            WriteExpr(like.Pattern, operand);
        }
        else if (_can.CaseInsensitiveLike)
        {
            WriteExpr(like.Value, operand);
            _w.Write(not + " ILIKE ");
            WriteExpr(like.Pattern, operand);
        }
        else
        {
            _w.Write("LOWER(");
            WriteExpr(like.Value, 0);
            _w.Write(")" + not + " LIKE LOWER(");
            WriteExpr(like.Pattern, 0);
            _w.Write(")");
        }
    }

    private void WriteConcat(Concat concat)
    {
        if (concat.Parts.Count < 2)
        {
            Problem("concat-arity", "Concatenation needs at least two parts.", concat);
        }

        if (_can.ConcatStyle == ConcatStyle.ConcatFunction)
        {
            _w.Write("CONCAT(");
            WriteList(concat.Parts);
            _w.Write(")");
            return;
        }

        for (var i = 0; i < concat.Parts.Count; i++)
        {
            if (i > 0)
            {
                _w.Write(" || ");
            }

            WriteExpr(concat.Parts[i], Precedence.Primary);
        }
    }

    private void WriteCall(FunctionCall call)
    {
        _w.Write(call.Name + "(" + (call.Distinct ? "DISTINCT " : string.Empty));
        WriteList(call.Args);
        _w.Write(")");
    }

    private void WriteCase(Case c)
    {
        _w.Write("CASE");
        if (c.Operand is not null)
        {
            _w.Write(" ");
            WriteExpr(c.Operand, 0);
        }

        foreach (var when in c.Whens)
        {
            _w.Write(" WHEN ");
            WriteExpr(when.When, 0, c.Operand is null);
            _w.Write(" THEN ");
            WriteExpr(when.Then, 0);
        }

        if (c.Else is not null)
        {
            _w.Write(" ELSE ");
            WriteExpr(c.Else, 0);
        }

        _w.Write(" END");
    }

    private void WriteWindowCall(WindowCall call)
    {
        Require(_can.WindowFunctions, "window-functions", "window functions", call);
        WriteCall(call.Function);
        var spec = call.Over;
        if (spec.BaseWindow is not null)
        {
            Require(_can.NamedWindow, "named-window", "named windows", call);
        }

        if (spec is { BaseWindow: { } name, PartitionBy.Count: 0, OrderBy.Count: 0, Frame: null })
        {
            _w.Write(" OVER " + Alias(name));
            return;
        }

        _w.Write(" OVER ");
        WriteWindowSpec(spec);
    }

    private void WriteWindowSpec(WindowSpec spec)
    {
        _w.Write("(");
        var separator = string.Empty;
        if (spec.BaseWindow is { } baseWindow)
        {
            _w.Write(Alias(baseWindow));
            separator = " ";
        }

        if (spec.PartitionBy.Count > 0)
        {
            _w.Write(separator + "PARTITION BY ");
            WriteList(spec.PartitionBy);
            separator = " ";
        }

        if (spec.OrderBy.Count > 0)
        {
            _w.Write(separator);
            WriteOrderBy(spec.OrderBy);
            separator = " ";
        }

        if (spec.Frame is { } frame)
        {
            _w.Write(separator);
            WriteFrame(frame);
        }

        _w.Write(")");
    }

    private void WriteOrderBy(IReadOnlyList<OrderItem> items)
    {
        _w.Write("ORDER BY ");
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                _w.Write(", ");
            }

            WriteExpr(items[i].Expr, 0);
            _w.Write(items[i].Descending ? " DESC" : string.Empty);
        }
    }

    private void WriteFrame(Frame frame)
    {
        var offsetBound = frame.Start.Kind is BoundKind.Preceding or BoundKind.Following
            || frame.End?.Kind is BoundKind.Preceding or BoundKind.Following;
        if (frame.Start.Kind == BoundKind.UnboundedFollowing
            || (frame.End is null && frame.Start.Kind == BoundKind.Following)
            || frame.End?.Kind == BoundKind.UnboundedPreceding
            || (frame.End is { } end && EndsBeforeStart(frame.Start, end)))
        {
            Problem("invalid-frame", "A frame cannot start at UNBOUNDED FOLLOWING or end before its start.", frame);
        }

        if (frame.Unit == FrameUnit.Groups)
        {
            Require(_can.GroupsFrame, "groups-frame", "GROUPS frames", frame);
        }

        if (frame.Unit == FrameUnit.Range && offsetBound)
        {
            Require(_can.RangeWithOffset, "range-offset", "RANGE frames with an offset", frame);
        }

        _w.Write(frame.Unit.ToString().ToUpperInvariant() + " ");
        _w.Write(frame.End is null ? Bound(frame.Start) : $"BETWEEN {Bound(frame.Start)} AND {Bound(frame.End)}");
        if (frame.Exclude is { } exclude)
        {
            Require(_can.FrameExclude, "frame-exclude", "frame EXCLUDE", frame);
            _w.Write(exclude switch
            {
                FrameExclude.CurrentRow => " EXCLUDE CURRENT ROW",
                FrameExclude.Group => " EXCLUDE GROUP",
                FrameExclude.Ties => " EXCLUDE TIES",
                FrameExclude.NoOthers => " EXCLUDE NO OTHERS",
                _ => throw new ArgumentOutOfRangeException(nameof(frame), exclude, null),
            });
        }
    }

    // Bounds order by kind; within PRECEDING a larger offset is earlier, within FOLLOWING a smaller one.
    private static bool EndsBeforeStart(FrameBound start, FrameBound end) => end.Kind == start.Kind
        ? (end.Kind == BoundKind.Preceding && end.Offset > start.Offset) || (end.Kind == BoundKind.Following && end.Offset < start.Offset)
        : end.Kind < start.Kind;

    private string Bound(FrameBound bound)
    {
        if (bound.Kind is BoundKind.Preceding or BoundKind.Following && bound.Offset is null)
        {
            Problem("invalid-frame-offset", "A PRECEDING or FOLLOWING bound needs an offset.", bound);
        }

        return bound.Kind switch
        {
            BoundKind.UnboundedPreceding => "UNBOUNDED PRECEDING",
            BoundKind.Preceding => $"{bound.Offset} PRECEDING",
            BoundKind.CurrentRow => "CURRENT ROW",
            BoundKind.Following => $"{bound.Offset} FOLLOWING",
            BoundKind.UnboundedFollowing => "UNBOUNDED FOLLOWING",
            _ => throw new ArgumentOutOfRangeException(nameof(bound), bound.Kind, null),
        };
    }

    private void WriteList(IReadOnlyList<Expr> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                _w.Write(", ");
            }

            WriteExpr(items[i], 0);
        }
    }

    private static string Operator(BinaryOp op) => op switch
    {
        BinaryOp.And => "AND",
        BinaryOp.Or => "OR",
        BinaryOp.Eq => "=",
        BinaryOp.NotEq => "<>",
        BinaryOp.Lt => "<",
        BinaryOp.LtEq => "<=",
        BinaryOp.Gt => ">",
        BinaryOp.GtEq => ">=",
        BinaryOp.Add => "+",
        BinaryOp.Subtract => "-",
        BinaryOp.Multiply => "*",
        BinaryOp.Divide => "/",
        BinaryOp.Modulo => "%",
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, null),
    };

    private string TableName(TableName name)
    {
        var table = _dialect.QuoteIdentifier(name.Name);
        return name.Schema is null ? table : _dialect.QuoteIdentifier(name.Schema) + "." + table;
    }

    private string AliasClause(string? alias)
    {
        return alias is null ? string.Empty : " AS " + Alias(alias);
    }

    // Table aliases stay bare when they are plain identifiers (`r.[FullName]`) that case folding cannot change;
    // anything else, and any reserved word of the four dialects, is quoted.
    private string Alias(string alias)
    {
        var simple = _can.UnquotedFoldsToLower ? LowerCaseName().IsMatch(alias) : SimpleName().IsMatch(alias);
        return simple && !ReservedWords.Contains(alias) ? alias : _dialect.QuoteIdentifier(alias);
    }

    private void Require(bool supported, string code, string feature, object node)
    {
        if (!supported)
        {
            Problem(code, $"{_dialect.Id} does not support {feature} (at this server version).", node);
        }
    }

    private void Problem(string code, string message, object node)
    {
        _problems.Add(new GenerationProblem(code, message, node));
    }
}
