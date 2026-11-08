namespace DataTray.Extensions.QueryBuilder.Model;

public sealed record JoinSource(Source Left, JoinType Type, Source Right, Expr? On) : Source;
