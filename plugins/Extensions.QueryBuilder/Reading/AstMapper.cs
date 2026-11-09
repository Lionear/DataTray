using System.Globalization;
using SqlParser;
using Ast = SqlParser.Ast;
using M = DataTray.Extensions.QueryBuilder.Model;

namespace DataTray.Extensions.QueryBuilder.Reading;

/// <summary>
/// Maps a SqlParserCS query to the model. A node the model has no place for becomes a Fragment or FragmentSource
/// holding the node's SQL; a clause of a query itself, or a node no text can keep, throws Refused.
/// </summary>
internal sealed class AstMapper(string providerId)
{
    private readonly bool _postgres = providerId == "postgres";
    private readonly bool _mysql = providerId == "mysql";
    private readonly bool _sqlite = providerId == "sqlite";
    private readonly List<string> _cteScope = [];

    public int FragmentCount { get; private set; }

    public M.Query Query(Ast.Query q)
    {
        if (q.Fetch is not null || q.Locks is not null || q.LimitBy is not null || q.ForClause is not null || q.Settings is not null || q.FormatClause is not null)
        {
            throw new Refused("a FETCH, FOR, locking or settings clause");
        }
        var scopeMark = _cteScope.Count;
        try
        {
            var with = q.With is null ? null : With(q.With);
            var body = Body(q.Body);
            var orderBy = q.OrderBy is null ? [] : OrderItems(q.OrderBy.Expressions ?? [], q.OrderBy.Interpolate is not null);
            return new M.Query(with, body, orderBy, Limit(q.Limit, q.Offset));
        }
        finally
        {
            _cteScope.RemoveRange(scopeMark, _cteScope.Count - scopeMark);
        }
    }

    private M.With With(Ast.With with)
    {
        var ctes = new List<M.Cte>();
        foreach (var cte in with.CteTables)
        {
            if (cte.Materialized is not null || cte.From is not null)
            {
                throw new Refused("a MATERIALIZED common table expression");
            }
            var name = Name(cte.Alias.Name);
            if (with.Recursive)
            {
                _cteScope.Add(name);
            }
            var columns = cte.Alias.Columns is { Count: > 0 } c ? new M.ValueList<string>(c.Select(Name)) : null;
            ctes.Add(new M.Cte(name, columns, Query(cte.Query)));
            if (!with.Recursive)
            {
                _cteScope.Add(name);
            }
        }
        return new M.With(with.Recursive, ctes.ToArray());
    }

    private M.QueryBody Body(Ast.SetExpression body) => body switch
    {
        Ast.SetExpression.SelectExpression s => Select(s.Select),
        // Parentheses around a plain query are only grouping; the model's tree carries that.
        Ast.SetExpression.QueryExpression { Query: { With: null, OrderBy: null, Limit: null, Offset: null, Fetch: null, Locks: null, LimitBy: null, ForClause: null, Settings: null, FormatClause: null } q } => Body(q.Body),
        Ast.SetExpression.QueryExpression q => new M.NestedQuery(Query(q.Query)),
        Ast.SetExpression.ValuesExpression v => new M.ValuesBody(v.Values.Rows.Select(r => new M.ValueList<M.Expr>(r.Select(Expr))).ToArray()),
        Ast.SetExpression.SetOperation op => SetOperation(op),
        _ => throw new Refused("this kind of query body"),
    };

    private M.QueryBody SetOperation(Ast.SetExpression.SetOperation op)
    {
        if (!_sqlite)
        {
            return new M.SetOperation(Body(op.Left), SetOp(op), op.SetQuantifier == Ast.SetQuantifier.All, Body(op.Right));
        }
        // SQLite gives all compound operators one precedence, left to right; the parser binds INTERSECT tighter.
        var operands = new List<Ast.SetExpression>();
        var ops = new List<Ast.SetExpression.SetOperation>();
        Flatten(op, operands, ops);
        var body = Body(operands[0]);
        for (var i = 0; i < ops.Count; i++)
        {
            body = new M.SetOperation(body, SetOp(ops[i]), ops[i].SetQuantifier == Ast.SetQuantifier.All, Body(operands[i + 1]));
        }
        return body;
    }

    private static void Flatten(Ast.SetExpression e, List<Ast.SetExpression> operands, List<Ast.SetExpression.SetOperation> ops)
    {
        if (e is Ast.SetExpression.SetOperation op)
        {
            Flatten(op.Left, operands, ops);
            ops.Add(op);
            Flatten(op.Right, operands, ops);
            return;
        }
        operands.Add(e);
    }

    private static M.SetOp SetOp(Ast.SetExpression.SetOperation op) => (op.Op, op.SetQuantifier) switch
    {
        (_, not (Ast.SetQuantifier.All or Ast.SetQuantifier.Distinct or Ast.SetQuantifier.None)) => throw new Refused("BY NAME set operations"),
        (Ast.SetOperator.Union, _) => M.SetOp.Union,
        (Ast.SetOperator.Intersect, _) => M.SetOp.Intersect,
        (Ast.SetOperator.Except, _) => M.SetOp.Except,
        _ => throw new Refused("this set operator"),
    };

    private M.Select Select(Ast.Select s)
    {
        if (s.Top is not null || s.Into is not null || s.LateralViews is not null || s.PreWhere is not null || s.ClusterBy is not null
            || s.DistributeBy is not null || s.SortBy is not null || s.QualifyBy is not null || s.ConnectBy is not null || s.ValueTableMode is not null)
        {
            throw new Refused("a clause the builder has no place for");
        }
        var distinct = s.Distinct switch
        {
            null => false,
            Ast.DistinctFilter.Distinct => true,
            _ => throw new Refused("DISTINCT ON"),
        };
        var groupBy = s.GroupBy switch
        {
            null => [],
            Ast.GroupByExpression.Expressions { Modifiers: null or { Count: 0 } } g => g.ColumnNames.Select(Expr).ToArray(),
            _ => throw new Refused("GROUP BY ALL or a grouping modifier"),
        };
        return new M.Select(
            distinct,
            s.Projection.Select(SelectItem).ToArray(),
            (s.From ?? []).Select(Source).ToArray(),
            s.Selection is null ? null : Expr(s.Selection),
            groupBy,
            s.Having is null ? null : Expr(s.Having),
            (s.NamedWindow ?? []).Select(NamedWindow).ToArray());
    }

    private M.SelectItem SelectItem(Ast.SelectItem item) => item switch
    {
        Ast.SelectItem.UnnamedExpression u => new M.SelectItem(Expr(u.Expression), null),
        Ast.SelectItem.ExpressionWithAlias a => new M.SelectItem(Expr(a.Expression), Name(a.Alias)),
        Ast.SelectItem.Wildcard w when w.WildcardAdditionalOptions == new Ast.WildcardAdditionalOptions() => new M.SelectItem(new M.Star(null), null),
        Ast.SelectItem.QualifiedWildcard w when w.WildcardAdditionalOptions == new Ast.WildcardAdditionalOptions() && w.Name.Values.Count == 1 =>
            new M.SelectItem(new M.Star(Name(w.Name.Values[0])), null),
        _ => new M.SelectItem(Fragment(item), null),
    };

    private M.NamedWindow NamedWindow(Ast.NamedWindowDefinition w) => w.WindowSpec switch
    {
        Ast.NamedWindowExpression.NamedWindowSpec spec => new M.NamedWindow(Name(w.Name), WindowSpec(spec.Spec)),
        Ast.NamedWindowExpression.NamedWindow other => new M.NamedWindow(Name(w.Name), new M.WindowSpec(Name(other.Expression), [], [], null)),
        _ => throw new Unrepresentable("this window definition"),
    };

    private M.ValueList<M.OrderItem> OrderItems(IEnumerable<Ast.OrderByExpression> items, bool interpolate)
    {
        if (interpolate)
        {
            throw new Refused("INTERPOLATE");
        }
        return items.Select(o => o.NullsFirst is null && o.WithFill is null
            ? new M.OrderItem(Expr(o.Expression), o.Asc == false)
            : new M.OrderItem(Fragment(o), false)).ToArray();
    }

    private static M.RowLimit? Limit(Ast.Expression? limit, Ast.Offset? offset) => (limit, offset) switch
    {
        (null, null) => null,
        (not null, null) => new M.RowLimit(Count(limit), null),
        (not null, not null) => new M.RowLimit(Count(limit), Count(offset.Value)),
        _ => throw new Refused("an OFFSET without a row count"),
    };

    private static long Count(Ast.Expression e) => WholeNumber(e) ?? throw new Refused("a row count that is not a whole number");

    private static long? WholeNumber(Ast.Expression e) =>
        e is Ast.Expression.LiteralValue { Value: Ast.Value.Number n } && long.TryParse(n.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            ? count
            : null;

    // Sources

    private M.Source Source(Ast.TableWithJoins twj)
    {
        var mark = FragmentCount;
        try
        {
            var source = Factor(twj.Relation ?? throw new Unrepresentable("an empty source"));
            foreach (var join in twj.Joins ?? [])
            {
                source = Join(source, join);
            }
            return source;
        }
        catch (Unrepresentable)
        {
            FragmentCount = mark;
            return FragmentSource(twj);
        }
    }

    private M.Source Join(M.Source left, Ast.Join join)
    {
        var right = Factor(join.Relation ?? throw new Unrepresentable("an empty join"));
        return join.JoinOperator switch
        {
            Ast.JoinOperator.Inner j => new M.JoinSource(left, M.JoinType.Inner, right, On(j.JoinConstraint)),
            Ast.JoinOperator.LeftOuter j => new M.JoinSource(left, M.JoinType.Left, right, On(j.JoinConstraint)),
            Ast.JoinOperator.RightOuter j => new M.JoinSource(left, M.JoinType.Right, right, On(j.JoinConstraint)),
            Ast.JoinOperator.FullOuter j => new M.JoinSource(left, M.JoinType.Full, right, On(j.JoinConstraint)),
            Ast.JoinOperator.CrossJoin => new M.JoinSource(left, M.JoinType.Cross, right, null),
            Ast.JoinOperator.CrossApply => new M.JoinSource(left, M.JoinType.CrossApply, right, null),
            Ast.JoinOperator.OuterApply => new M.JoinSource(left, M.JoinType.OuterApply, right, null),
            _ => throw new Unrepresentable("this join"),
        };
    }

    private M.Expr On(Ast.JoinConstraint constraint) =>
        constraint is Ast.JoinConstraint.On on ? Expr(on.Expression) : throw new Unrepresentable("a join without ON");

    private M.Source Factor(Ast.TableFactor factor)
    {
        if (factor.Alias is { Columns.Count: > 0 })
        {
            throw new Unrepresentable("an alias with column names");
        }
        var alias = factor.Alias is null ? null : Name(factor.Alias.Name);
        return factor switch
        {
            Ast.TableFactor.Table t when t.Args is null && t.WithHints is null && t.Version is null && t.Partitions is null && !t.WithOrdinality => Table(t.Name, alias),
            Ast.TableFactor.Derived d when alias is not null => new M.SubquerySource(Query(d.SubQuery), alias, d.Lateral),
            Ast.TableFactor.NestedJoin { TableWithJoins: { } inner } when alias is null => Source(inner),
            _ => throw new Unrepresentable("this source"),
        };
    }

    private M.Source Table(Ast.ObjectName name, string? alias)
    {
        var parts = name.Values;
        if (parts.Count == 1 && _cteScope.Contains(Name(parts[0]), _postgres ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase))
        {
            return new M.CteSource(Name(parts[0]), alias);
        }
        return parts.Count switch
        {
            1 => new M.TableSource(new M.TableName(null, Name(parts[0])), alias),
            2 => new M.TableSource(new M.TableName(Name(parts[0]), Name(parts[1])), alias),
            _ => throw new Unrepresentable("a name with more than two parts"),
        };
    }

    // Expressions

    private M.Expr Expr(Ast.Expression e)
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

    private M.Expr Map(Ast.Expression e) => e switch
    {
        Ast.Expression.Identifier i => new M.ColumnRef(null, Name(i.Ident)),
        Ast.Expression.CompoundIdentifier { Idents.Count: 2 } c => new M.ColumnRef(Name(c.Idents[0]), Name(c.Idents[1])),
        Ast.Expression.Wildcard => new M.Star(null),
        Ast.Expression.QualifiedWildcard { Name.Values.Count: 1 } w => new M.Star(Name(w.Name.Values[0])),
        Ast.Expression.LiteralValue v => Literal(v.Value),
        Ast.Expression.Nested n => Map(n.Expression),
        Ast.Expression.UnaryOp { Op: Ast.UnaryOperator.Not } u => new M.Unary(M.UnaryOp.Not, Expr(u.Expression)),
        Ast.Expression.UnaryOp { Op: Ast.UnaryOperator.Minus, Expression: Ast.Expression.LiteralValue { Value: Ast.Value.Number n } } => new M.Literal(M.LiteralKind.Number, "-" + n.Value),
        Ast.Expression.UnaryOp { Op: Ast.UnaryOperator.Minus } u => new M.Unary(M.UnaryOp.Negate, Expr(u.Expression)),
        Ast.Expression.BinaryOp { Op: Ast.BinaryOperator.StringConcat } b when !_mysql => new M.Concat([.. ConcatParts(b.Left), Expr(b.Right)]),
        Ast.Expression.BinaryOp b => new M.Binary(BinaryOp(b.Op), Expr(b.Left), Expr(b.Right)),
        Ast.Expression.Between b => new M.Between(Expr(b.Expression), Expr(b.Low), Expr(b.High), b.Negated),
        Ast.Expression.Like { EscapeChar: null, Any: false, Expression: { } value } l => Like(value, l.Pattern, l.Negated),
        Ast.Expression.ILike { EscapeChar: null, Any: false } l => new M.Like(Expr(l.Expression), Expr(l.Pattern), true, l.Negated),
        Ast.Expression.IsNull i => new M.IsNull(Expr(i.Expression), false),
        Ast.Expression.IsNotNull i => new M.IsNull(Expr(i.Expression), true),
        Ast.Expression.InList i => new M.InList(Expr(i.Expression), i.List.Select(Expr).ToArray(), i.Negated),
        Ast.Expression.InSubquery { Expression: { } value } i => new M.InSubquery(Expr(value), Query(i.SubQuery), i.Negated),
        Ast.Expression.Exists x => new M.Exists(Query(x.SubQuery), x.Negated),
        Ast.Expression.AnyOp { Right: Ast.Expression.Subquery s } a => new M.Quantified(Expr(a.Left), BinaryOp(a.CompareOp), M.Quantifier.Any, Query(s.Query)),
        Ast.Expression.AllOp { Right: Ast.Expression.Subquery s } a => new M.Quantified(Expr(a.Left), BinaryOp(a.CompareOp), M.Quantifier.All, Query(s.Query)),
        Ast.Expression.Subquery s => new M.ScalarSubquery(Query(s.Query)),
        Ast.Expression.Case c => new M.Case(
            c.Operand is null ? null : Expr(c.Operand),
            c.Conditions.Zip(c.Results, (w, t) => new M.CaseWhen(Expr(w), Expr(t))).ToArray(),
            c.ElseResult is null ? null : Expr(c.ElseResult)),
        Ast.Expression.Cast { Kind: Ast.CastKind.Cast or Ast.CastKind.DoubleColon, Format: null } c => new M.Cast(Expr(c.Expression), c.DataType.ToSql()),
        Ast.Expression.Function f => Function(f),
        _ => throw new Unrepresentable("this expression"),
    };

    private IEnumerable<M.Expr> ConcatParts(Ast.Expression e) => e is Ast.Expression.BinaryOp { Op: Ast.BinaryOperator.StringConcat } b
        ? [.. ConcatParts(b.Left), Expr(b.Right)]
        : [Expr(e)];

    // LOWER(x) LIKE LOWER(p) is how the generator writes a case-insensitive LIKE where there is no ILIKE (not PostgreSQL).
    private M.Expr Like(Ast.Expression value, Ast.Expression pattern, bool negated) =>
        !_postgres && LowerArgument(value) is { } v && LowerArgument(pattern) is { } p
            ? new M.Like(Expr(v), Expr(p), true, negated)
            : new M.Like(Expr(value), Expr(pattern), false, negated);

    private static Ast.Expression? LowerArgument(Ast.Expression e) =>
        e is Ast.Expression.Function { Name.Values: [{ QuoteStyle: null } name], Over: null, Filter: null, WithinGroup: null, Args: Ast.FunctionArguments.List { ArgumentList: { DuplicateTreatment: null, Clauses: null or { Count: 0 }, Args: [Ast.FunctionArg.Unnamed { FunctionArgExpression: Ast.FunctionArgExpression.FunctionExpression arg }] } } }
            && name.Value.Equals("LOWER", StringComparison.OrdinalIgnoreCase)
            ? arg.Expression
            : null;

    private M.Expr Function(Ast.Expression.Function f)
    {
        if (f.Name.Values is not [{ QuoteStyle: null } name] || f.Filter is not null || f.NullTreatment is not null || f.WithinGroup is { Count: > 0 }
            || f.Parameters is not (null or Ast.FunctionArguments.None)
            || f.Args is not Ast.FunctionArguments.List { ArgumentList: { Clauses: null or { Count: 0 } } list }
            || list.DuplicateTreatment is Ast.DuplicateTreatment.All)
        {
            throw new Unrepresentable("this function call");
        }
        var args = (list.Args ?? []).Select(Argument).ToArray();
        var distinct = list.DuplicateTreatment is Ast.DuplicateTreatment.Distinct;
        if (name.Value.Equals("VALUES", StringComparison.OrdinalIgnoreCase))
        {
            throw new Refused("a VALUES list read as a function call");
        }
        if (_mysql && f.Over is null && !distinct && args.Length >= 2 && name.Value.Equals("CONCAT", StringComparison.OrdinalIgnoreCase))
        {
            return new M.Concat(args);
        }
        var call = new M.FunctionCall(name.Value, args, distinct);
        return f.Over switch
        {
            null => call,
            Ast.WindowType.WindowSpecType spec => new M.WindowCall(call, WindowSpec(spec.Spec)),
            Ast.WindowType.NamedWindow named => new M.WindowCall(call, new M.WindowSpec(Name(named.Name), [], [], null)),
            _ => throw new Unrepresentable("this window"),
        };
    }

    private M.Expr Argument(Ast.FunctionArg arg) => arg switch
    {
        Ast.FunctionArg.Unnamed { FunctionArgExpression: Ast.FunctionArgExpression.FunctionExpression e } => Expr(e.Expression),
        Ast.FunctionArg.Unnamed { FunctionArgExpression: Ast.FunctionArgExpression.Wildcard } => new M.Star(null),
        Ast.FunctionArg.Unnamed { FunctionArgExpression: Ast.FunctionArgExpression.QualifiedWildcard { Name.Values.Count: 1 } w } => new M.Star(Name(w.Name.Values[0])),
        _ => throw new Unrepresentable("a named or qualified argument"),
    };

    private M.WindowSpec WindowSpec(Ast.WindowSpec spec) => new(
        spec.WindowName is null ? null : Name(spec.WindowName),
        (spec.PartitionBy ?? []).Select(Expr).ToArray(),
        OrderItems(spec.OrderBy ?? [], false),
        spec.WindowFrame is null ? null : Frame(spec.WindowFrame));

    private static M.Frame Frame(Ast.WindowFrame frame)
    {
        var unit = frame.Units switch
        {
            Ast.WindowFrameUnit.Rows => M.FrameUnit.Rows,
            Ast.WindowFrameUnit.Range => M.FrameUnit.Range,
            _ => M.FrameUnit.Groups,
        };
        var start = Bound(frame.StartBound ?? throw new Unrepresentable("a frame without a start"));
        return new M.Frame(unit, start, frame.EndBound is null ? null : Bound(frame.EndBound), null);
    }

    private static M.FrameBound Bound(Ast.WindowFrameBound bound) => bound switch
    {
        Ast.WindowFrameBound.CurrentRow => new M.FrameBound(M.BoundKind.CurrentRow, null),
        Ast.WindowFrameBound.Preceding { Expression: null } => new M.FrameBound(M.BoundKind.UnboundedPreceding, null),
        Ast.WindowFrameBound.Following { Expression: null } => new M.FrameBound(M.BoundKind.UnboundedFollowing, null),
        Ast.WindowFrameBound.Preceding { Expression: { } offset } => new M.FrameBound(M.BoundKind.Preceding, WholeNumber(offset) ?? throw new Unrepresentable("a frame offset")),
        Ast.WindowFrameBound.Following { Expression: { } offset } => new M.FrameBound(M.BoundKind.Following, WholeNumber(offset) ?? throw new Unrepresentable("a frame offset")),
        _ => throw new Unrepresentable("this frame bound"),
    };

    private static M.Literal Literal(Ast.Value value) => value switch
    {
        Ast.Value.Number n => new M.Literal(M.LiteralKind.Number, n.Value),
        Ast.Value.SingleQuotedString s => new M.Literal(M.LiteralKind.String, s.Value),
        Ast.Value.Boolean b => new M.Literal(M.LiteralKind.Boolean, b.Value ? "true" : "false"),
        Ast.Value.Null => new M.Literal(M.LiteralKind.Null, "NULL"),
        _ => throw new Unrepresentable("this literal"),
    };

    private static M.BinaryOp BinaryOp(Ast.BinaryOperator op) => op switch
    {
        Ast.BinaryOperator.And => M.BinaryOp.And,
        Ast.BinaryOperator.Or => M.BinaryOp.Or,
        Ast.BinaryOperator.Eq => M.BinaryOp.Eq,
        Ast.BinaryOperator.NotEq => M.BinaryOp.NotEq,
        Ast.BinaryOperator.Lt => M.BinaryOp.Lt,
        Ast.BinaryOperator.LtEq => M.BinaryOp.LtEq,
        Ast.BinaryOperator.Gt => M.BinaryOp.Gt,
        Ast.BinaryOperator.GtEq => M.BinaryOp.GtEq,
        Ast.BinaryOperator.Plus => M.BinaryOp.Add,
        Ast.BinaryOperator.Minus => M.BinaryOp.Subtract,
        Ast.BinaryOperator.Multiply => M.BinaryOp.Multiply,
        Ast.BinaryOperator.Divide => M.BinaryOp.Divide,
        Ast.BinaryOperator.Modulo => M.BinaryOp.Modulo,
        _ => throw new Unrepresentable("this operator"),
    };

    // PostgreSQL folds unquoted names to lower case, ASCII letters only; the model holds the name the server sees.
    private string Name(Ast.Ident ident) =>
        _postgres && ident.QuoteStyle is null ? string.Concat(ident.Value.Select(ch => ch is >= 'A' and <= 'Z' ? (char)(ch + 32) : ch)) : ident.Value;

    // A Fragment is only safe when the parser ranks its operator the way the server does; otherwise refuse
    // (Refused here, or a ScriptScanner finding for tokens the parser misreads).
    private M.Fragment Fragment(IWriteSql node)
    {
        FragmentCount++;
        return new M.Fragment(node.ToSql());
    }

    private M.FragmentSource FragmentSource(IWriteSql node)
    {
        FragmentCount++;
        return new M.FragmentSource(node.ToSql());
    }
}
