namespace DataTray.Extensions.QueryBuilder.Model;

public sealed record WindowSpec(string? BaseWindow, ValueList<Expr> PartitionBy, ValueList<OrderItem> OrderBy, Frame? Frame);
