namespace DataTray.Extensions.QueryBuilder.Model;

public sealed record TableSource(TableName Name, string? Alias) : Source;
