namespace DataTray.Extensions.QueryBuilder.Model;

public sealed record ValuesSource(ValueList<ValueList<Expr>> Rows) : InsertSource;
