namespace DataTray.Extensions.QueryBuilder.Model;

public sealed record FunctionCall(string Name, ValueList<Expr> Args, bool Distinct) : Expr;
