namespace DataTray.Extensions.QueryBuilder.Model;

public sealed record Like(Expr Value, Expr Pattern, bool CaseInsensitive, bool Not) : Expr;
