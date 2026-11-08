namespace DataTray.Extensions.QueryBuilder.Model;

public sealed record Unary(UnaryOp Op, Expr Operand) : Expr;
