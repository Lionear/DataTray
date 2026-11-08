using DataTray.Extensions.QueryBuilder.Dialects;
using DataTray.Extensions.QueryBuilder.Generation;
using DataTray.Extensions.QueryBuilder.Model;

namespace DataTray.Extensions.QueryBuilder.Tests.Generation;

// Model shorthands for the generator tests; construction only, no logic.
internal static class Build
{
    public static ColumnRef Col(string name) => new(null, name);

    public static ColumnRef Col(string qualifier, string name) => new(qualifier, name);

    public static Literal Num(string text) => new(LiteralKind.Number, text);

    public static Literal Str(string text) => new(LiteralKind.String, text);

    public static Binary Bin(BinaryOp op, Expr left, Expr right) => new(op, left, right);

    public static Unary Not(Expr operand) => new(UnaryOp.Not, operand);

    public static Unary Neg(Expr operand) => new(UnaryOp.Negate, operand);

    public static TableSource Table(string name, string? alias) => new(new TableName("dbo", name), alias);

    public static Query Query(Expr? where, Source[] from, params SelectItem[] items) =>
        new(null, new Select(false, items, from, where, [], null, []), [], null);

    public static Query Query(params Expr[] items) =>
        new(null, new Select(false, items.Select(i => new SelectItem(i, null)).ToArray(), [], null, [], null, []), [], null);

    public static GenerationResult Generate(string provider, Statement statement) =>
        SqlGenerator.Generate(statement, SqlDialects.For(provider, null));

    public static GenerationResult Generate(string provider, string? version, Query query) =>
        SqlGenerator.Generate(new QueryStatement(query), SqlDialects.For(provider, version));

    public static GenerationResult Generate(string provider, string? version, Expr item) =>
        Generate(provider, version, Query(item));

    public static string[] Codes(GenerationResult result) => result.Problems.Select(p => p.Code).ToArray();
}
