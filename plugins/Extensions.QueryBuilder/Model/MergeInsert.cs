namespace DataTray.Extensions.QueryBuilder.Model;

public sealed record MergeInsert(ValueList<string> Columns, ValueList<Expr> Values) : MergeAction;
