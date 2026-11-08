namespace DataTray.Extensions.QueryBuilder.Model;

public sealed record IsNull(Expr Value, bool Not) : Expr;
