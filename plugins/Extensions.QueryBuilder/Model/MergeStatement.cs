namespace DataTray.Extensions.QueryBuilder.Model;

public sealed record MergeStatement(TableName Target, string Alias, Source Using, Expr On, ValueList<MergeClause> Clauses, Returning? Returning) : Statement;
