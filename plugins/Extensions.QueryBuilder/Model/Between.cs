namespace DataTray.Extensions.QueryBuilder.Model;

public sealed record Between(Expr Value, Expr Low, Expr High, bool Not) : Expr;
