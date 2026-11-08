namespace DataTray.Extensions.QueryBuilder.Model;

public sealed record Cte(string Name, ValueList<string>? Columns, Query Query);
