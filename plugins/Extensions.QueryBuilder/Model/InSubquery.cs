namespace DataTray.Extensions.QueryBuilder.Model;

public sealed record InSubquery(Expr Value, Query Query, bool Not) : Expr;
