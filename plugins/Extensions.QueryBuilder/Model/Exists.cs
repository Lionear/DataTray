namespace DataTray.Extensions.QueryBuilder.Model;

public sealed record Exists(Query Query, bool Not) : Expr;
