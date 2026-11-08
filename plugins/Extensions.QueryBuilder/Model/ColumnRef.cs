namespace DataTray.Extensions.QueryBuilder.Model;

public sealed record ColumnRef(string? Qualifier, string Name) : Expr;
