namespace DataTray.Extensions.QueryBuilder.Model;

public sealed record Query(With? With, QueryBody Body, ValueList<OrderItem> OrderBy, RowLimit? Limit);
