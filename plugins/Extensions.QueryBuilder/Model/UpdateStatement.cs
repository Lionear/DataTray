namespace DataTray.Extensions.QueryBuilder.Model;

public sealed record UpdateStatement(With? With, TableName Target, string? Alias, ValueList<Assignment> Set, ValueList<Source> From, Expr? Where, Returning? Returning) : Statement;
