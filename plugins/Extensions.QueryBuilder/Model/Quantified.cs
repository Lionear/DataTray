namespace DataTray.Extensions.QueryBuilder.Model;

public sealed record Quantified(Expr Value, BinaryOp Comparison, Quantifier Quantifier, Query Query) : Expr;
