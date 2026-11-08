using DataTray.Extensions.QueryBuilder.Dialects;
using DataTray.Extensions.QueryBuilder.Model;

namespace DataTray.Extensions.QueryBuilder.Generation;

// Higher binds tighter. A child is parenthesized when its level is below what its parent requires; a right
// operand at equal level is too, so only left-deep chains print flat and the tree reads back unchanged.
internal static class Precedence
{
    // A fragment's inner structure is unknown, so any operator around it gets parentheses.
    public const int Fragment = 0;
    public const int Or = 1;
    public const int And = 2;
    public const int Not = 3;
    public const int Comparison = 4;
    // `||` sits between comparison and arithmetic: PostgreSQL and SQLite disagree on its rank against `+`/`*`.
    public const int Concat = 5;
    public const int Additive = 6;
    public const int Multiplicative = 7;
    public const int Unary = 8;
    public const int Primary = 9;

    public static int Of(BinaryOp op) => op switch
    {
        BinaryOp.Or => Or,
        BinaryOp.And => And,
        BinaryOp.Add or BinaryOp.Subtract => Additive,
        BinaryOp.Multiply or BinaryOp.Divide or BinaryOp.Modulo => Multiplicative,
        _ => Comparison,
    };

    public static int Of(Expr expr, ConcatStyle concat) => expr switch
    {
        Binary b => Of(b.Op),
        Model.Unary { Op: UnaryOp.Not } => Not,
        Model.Unary => Unary,
        Between or Like or IsNull or InList or InSubquery or Quantified or Exists => Comparison,
        Literal { Kind: LiteralKind.Number } l when l.Text.StartsWith('-') => Unary,
        Model.Concat when concat == ConcatStyle.Pipes => Concat,
        Model.Fragment => Fragment,
        _ => Primary,
    };

}
