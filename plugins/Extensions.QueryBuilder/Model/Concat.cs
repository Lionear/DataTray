namespace DataTray.Extensions.QueryBuilder.Model;

public sealed record Concat(ValueList<Expr> Parts) : Expr;
