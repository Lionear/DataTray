namespace DataTray.Extensions.QueryBuilder.Model;

public sealed record ValuesBody(ValueList<ValueList<Expr>> Rows) : QueryBody;
