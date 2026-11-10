namespace DataTray.Extensions.QueryBuilder.Model;

public sealed record DeleteStatement(With? With, TableName Target, string? Alias, ValueList<Source> Using, Expr? Where, Returning? Returning) : Statement;
