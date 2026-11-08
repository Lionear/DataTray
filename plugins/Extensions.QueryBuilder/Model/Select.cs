namespace DataTray.Extensions.QueryBuilder.Model;

public sealed record Select(bool Distinct, ValueList<SelectItem> Items, ValueList<Source> From, Expr? Where, ValueList<Expr> GroupBy, Expr? Having, ValueList<NamedWindow> Windows) : QueryBody;
