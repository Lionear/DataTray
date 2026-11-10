namespace DataTray.Extensions.QueryBuilder.Model;

public sealed record SelectItem(Expr Expr, string? Alias);
