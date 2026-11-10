namespace DataTray.Extensions.QueryBuilder.Model;

public sealed record Cast(Expr Value, string TypeName) : Expr;
