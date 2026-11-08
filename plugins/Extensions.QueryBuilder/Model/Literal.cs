namespace DataTray.Extensions.QueryBuilder.Model;

public sealed record Literal(LiteralKind Kind, string Text) : Expr;
