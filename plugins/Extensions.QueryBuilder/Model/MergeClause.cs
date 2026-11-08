namespace DataTray.Extensions.QueryBuilder.Model;

public sealed record MergeClause(MergeMatch Match, Expr? Condition, MergeAction Action);
