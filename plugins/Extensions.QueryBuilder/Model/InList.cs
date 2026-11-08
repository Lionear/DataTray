namespace DataTray.Extensions.QueryBuilder.Model;

public sealed record InList(Expr Value, ValueList<Expr> Items, bool Not) : Expr;
