namespace DataTray.Extensions.QueryBuilder.Model;

public sealed record SubquerySource(Query Query, string Alias, bool Lateral) : Source;
