namespace DataTray.Extensions.QueryBuilder.Model;

public sealed record WindowCall(FunctionCall Function, WindowSpec Over) : Expr;
