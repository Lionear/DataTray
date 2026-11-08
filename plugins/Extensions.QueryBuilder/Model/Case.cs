namespace DataTray.Extensions.QueryBuilder.Model;

public sealed record Case(Expr? Operand, ValueList<CaseWhen> Whens, Expr? Else) : Expr;
