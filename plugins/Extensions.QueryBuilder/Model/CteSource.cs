namespace DataTray.Extensions.QueryBuilder.Model;

public sealed record CteSource(string Name, string? Alias) : Source;
