namespace DataTray.Extensions.QueryBuilder.Model;

public sealed record SetOperation(QueryBody Left, SetOp Op, bool All, QueryBody Right) : QueryBody;
