namespace DataTray.Extensions.QueryBuilder.Model;

public sealed record With(bool Recursive, ValueList<Cte> Ctes);
