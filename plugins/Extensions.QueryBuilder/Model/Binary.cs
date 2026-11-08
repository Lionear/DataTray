namespace DataTray.Extensions.QueryBuilder.Model;

public sealed record Binary(BinaryOp Op, Expr Left, Expr Right) : Expr;
